using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Security;
using BigInteger = Org.BouncyCastle.Math.BigInteger;

namespace Seamdesk.Transport;

/// <summary>The 8-digit code machine A shows: single use, valid 2 minutes, dead after 3 attempts.</summary>
public sealed class PairingCode
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly TimeProvider time;
    private readonly long issued;
    private int attempts;
    private bool used;

    public PairingCode(TimeProvider time)
    {
        this.time = time;
        issued = time.GetTimestamp();
        Code = RandomNumberGenerator.GetInt32(100_000_000).ToString("D8");
    }

    public string Code { get; }

    /// <summary>
    /// Every attempt counts, not only those that end in a wrong confirmation: an attacker who drops the connection
    /// before round 3 still spent a guess.
    /// </summary>
    internal bool TryBeginAttempt()
    {
        lock (this)
        {
            if (used || attempts >= MaxAttempts || time.GetElapsedTime(issued) >= Lifetime)
            {
                return false;
            }

            attempts++;
            return true;
        }
    }

    internal void Consume()
    {
        lock (this)
        {
            used = true;
        }
    }
}

/// <summary>
/// Pairing over a TLS 1.3 connection where neither certificate is pinned yet: Hello exchange, then J-PAKE
/// (BouncyCastle, NIST 3072-bit group, SHA-256) with the code as the password. The J-PAKE key confirmation (round 3)
/// is bound to both certificates as each side saw them, so a man in the middle, who must present his own certificates,
/// fails it. On success each side pins the other.
/// </summary>
public static class Pairing
{
    private const string DialerId = "seamdesk-pair-dialer";
    private const string ListenerId = "seamdesk-pair-listener";
    private const string WrongCode = "wrong pairing code";

    /// <summary>Machine B: the user typed <paramref name="code"/>. Returns the pin it added for machine A.</summary>
    public static async Task<Pin> DialAsync(SslStream ssl, LocalNode me, string code, string address, CancellationToken ct)
    {
        await using (ssl)
        {
            Hello hello = await Connection.ExchangeHelloAsync(ssl, me, ct);
            string peerSpki = Identity.SpkiOf(ssl.RemoteCertificate!);
            JPakeParticipant jpake = new(DialerId, code.ToCharArray(), JPakePrimeOrderGroups.NIST_3072, new Sha256Digest(), new SecureRandom());
            BigInteger key = await KeyAsync(ssl, jpake, me, peerRound1: null, dialerSpki: me.Identity.Spki, listenerSpki: peerSpki, ct);

            await FrameCodec.WriteAsync(ssl, Round3(jpake.CreateRound3PayloadToSend(key)), ct);
            Pake theirs = await Connection.ExpectAsync<Pake>(ssl, ct);
            Validate(() => jpake.ValidateRound3PayloadReceived(Round3(theirs), key), WrongCode);

            Pin pin = new(peerSpki, hello.MachineId, address);
            me.Pins.Add(pin);
            return pin;
        }
    }

    /// <summary>Machine A, showing <paramref name="code"/>. Returns the pin it added for machine B.</summary>
    public static async Task<Pin> RespondAsync(SslStream ssl, LocalNode me, PairingCode code, string address, CancellationToken ct)
    {
        await using (ssl)
        {
            Hello hello = await Connection.ExchangeHelloAsync(ssl, me, ct);

            // Only a peer that starts J-PAKE is pairing. A normal dial from a peer we do not know sends Ready instead
            // and must not use up the code.
            if (await FrameCodec.ReadAsync(ssl, ct) is not Pake { Round: 1 } round1)
            {
                string reason = "not paired with this machine (unknown certificate): pair again";
                await Connection.RefuseAsync(ssl, me, reason, ct);
                throw new RefusedException(reason);
            }

            if (!code.TryBeginAttempt())
            {
                string reason = $"no pairing code is active on {me.Hello.MachineId} (expired, used or too many tries)";
                await Connection.RefuseAsync(ssl, me, reason, ct);
                throw new RefusedException(reason);
            }

            string peerSpki = Identity.SpkiOf(ssl.RemoteCertificate!);
            JPakeParticipant jpake = new(ListenerId, code.Code.ToCharArray(), JPakePrimeOrderGroups.NIST_3072, new Sha256Digest(), new SecureRandom());
            BigInteger key = await KeyAsync(ssl, jpake, me, round1, dialerSpki: peerSpki, listenerSpki: me.Identity.Spki, ct);

            // The dialer confirms first, so A never sends its confirmation to someone who has not proven the code.
            // (BouncyCastle wants each side's round 3 created before the peer's is validated.)
            Pake mine = Round3(jpake.CreateRound3PayloadToSend(key));
            Pake theirs = await Connection.ExpectAsync<Pake>(ssl, ct);
            try
            {
                jpake.ValidateRound3PayloadReceived(Round3(theirs), key);
            }
            catch (CryptoException)
            {
                await Connection.RefuseAsync(ssl, me, WrongCode, ct);
                throw new RefusedException(WrongCode);
            }

            code.Consume();
            Pin pin = new(peerSpki, hello.MachineId, address);
            me.Pins.Add(pin);
            await FrameCodec.WriteAsync(ssl, mine, ct);
            return pin;
        }
    }

    /// <summary>
    /// J-PAKE rounds 1 and 2 (the listener has already read the dialer's round 1), then the keying material that round 3 confirms: HKDF of the J-PAKE key with a label, the
    /// protocol version and both SPKI fingerprints in (dialer, listener) order (channel binding in the style of RFC 5929
    /// tls-server-end-point; .NET's SslStream exposes no RFC 9266 tls-exporter).
    /// </summary>
    private static async Task<BigInteger> KeyAsync(SslStream ssl, JPakeParticipant jpake, LocalNode me, Pake? peerRound1, string dialerSpki, string listenerSpki, CancellationToken ct)
    {
        JPakeRound1Payload r1 = jpake.CreateRound1PayloadToSend();
        await FrameCodec.WriteAsync(ssl, new Pake(1, r1.ParticipantId, Bytes([r1.Gx1, r1.Gx2, .. r1.KnowledgeProofForX1, .. r1.KnowledgeProofForX2])), ct);
        Pake p1 = Check(peerRound1 ?? await Connection.ExpectAsync<Pake>(ssl, ct), 1, 6);
        Validate(() => jpake.ValidateRound1PayloadReceived(new JPakeRound1Payload(p1.Participant, Big(p1, 0), Big(p1, 1), [Big(p1, 2), Big(p1, 3)], [Big(p1, 4), Big(p1, 5)])), "invalid J-PAKE round 1");

        JPakeRound2Payload r2 = jpake.CreateRound2PayloadToSend();
        await FrameCodec.WriteAsync(ssl, new Pake(2, r2.ParticipantId, Bytes([r2.A, .. r2.KnowledgeProofForX2s])), ct);
        Pake p2 = Check(await Connection.ExpectAsync<Pake>(ssl, ct), 2, 3);
        Validate(() => jpake.ValidateRound2PayloadReceived(new JPakeRound2Payload(p2.Participant, Big(p2, 0), [Big(p2, 1), Big(p2, 2)])), "invalid J-PAKE round 2");

        byte[] info = [.. "seamdesk pairing v1"u8, .. BigEndian(me.Hello.Protocol), .. Convert.FromHexString(dialerSpki), .. Convert.FromHexString(listenerSpki)];
        byte[] bound = HKDF.DeriveKey(HashAlgorithmName.SHA256, jpake.CalculateKeyingMaterial().ToByteArrayUnsigned(), 32, salt: [], info);
        return new BigInteger(1, bound);
    }

    private static Pake Check(Pake pake, int round, int values) =>
        pake.Round == round && pake.Values.Count == values
            ? pake
            : throw new ProtocolException($"expected J-PAKE round {round} with {values} values");

    private static Pake Round3(JPakeRound3Payload r3) => new(3, r3.ParticipantId, Bytes([r3.MacTag]));

    private static JPakeRound3Payload Round3(Pake pake) =>
        pake.Round == 3 && pake.Values.Count == 1
            ? new JPakeRound3Payload(pake.Participant, Big(pake, 0))
            : throw new ProtocolException("expected J-PAKE round 3");

    private static void Validate(Action check, string reason)
    {
        try
        {
            check();
        }
        catch (CryptoException)
        {
            throw new RefusedException(reason);
        }
    }

    private static byte[][] Bytes(BigInteger[] values) => [.. values.Select(v => v.ToByteArray())];

    private static BigInteger Big(Pake pake, int i) => new(pake.Values[i]);

    private static byte[] BigEndian(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
