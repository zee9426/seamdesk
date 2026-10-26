// Ported from Mouse Without Borders (PowerToys, src/modules/MouseWithoutBorders/App): Core/Encryption.cs, Core/DATA.cs,
// Core/PackageType.cs, Class/SocketStuff.cs at commit a9e1f0f20^ (the old format), and Core/Encryption.cs at 48c4bad07
// (the current format, 0.101 and later).
// Copyright (c) Microsoft Corporation. The Microsoft Corporation licenses this file to you under the MIT license.
// Changes for Seamdesk: plain byte offsets instead of an explicit-layout class; async-friendly block transforms.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Seamdesk.Mwb;

/// <summary>
/// Which MWB wire format a link speaks. <c>Old</c>: standalone MWB 2.x and PowerToys up to 0.100, one key and IV for every
/// connection (the two derive that key differently: <see cref="MwbKey.Standalone"/>). <c>Current</c>: PowerToys 0.101 and
/// later, a random salt and IV per stream, sent in the clear first.
/// </summary>
public enum MwbFormat { Old, Current }

/// <summary>MWB's package types (Core/PackageType.cs). <c>Matrix</c> packages also carry flag bits 2 and 4.</summary>
public enum PackageType : byte
{
    Hi = 2,
    Hello = 3,
    ByeBye = 4,
    Heartbeat = 20,
    Awake = 21,
    HideMouse = 50,
    HeartbeatEx = 51,
    HeartbeatExL2 = 52,
    HeartbeatExL3 = 53,
    Clipboard = 69,
    ClipboardDragDrop = 70,
    ClipboardDragDropEnd = 71,
    ExplorerDragDrop = 72,
    ClipboardCapture = 73,
    CaptureScreenCommand = 74,
    ClipboardDragDropOperation = 75,
    ClipboardDataEnd = 76,
    MachineSwitched = 77,
    ClipboardAsk = 78,
    ClipboardPush = 79,
    NextMachine = 121,
    Keyboard = 122,
    Mouse = 123,
    ClipboardText = 124,
    ClipboardImage = 125,
    Handshake = 126,
    HandshakeAck = 127,
    Matrix = 128,
    Error = 0xFE,
    Invalid = 0xFF,
}

/// <summary>
/// One MWB package (Core/DATA.cs): 32 bytes, or 64 for the "big" types. Byte 0 is the type, byte 1 a checksum and bytes
/// 2-3 the top of the key's magic number; then Id, Src, Des, and from offset 16 either the mouse data (X, Y,
/// WheelDelta, dwFlags), the keyboard data at 24 (wVk, dwFlags) or four handshake words; big packages carry the sender's
/// name at 32-63. Little-endian, as MWB's in-memory layout on Windows.
/// </summary>
public sealed class Package
{
    public const int Small = 32;
    public const int Big = 64;

    public Package(PackageType type) => Type = type;

    public Package(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, Big)).CopyTo(Bytes);

    public byte[] Bytes { get; } = new byte[Big];

    public PackageType Type
    {
        get => (PackageType)Bytes[0];
        set => Bytes[0] = (byte)value;
    }

    public int Id { get => Get(4); set => Set(4, value); }

    public uint Src { get => (uint)Get(8); set => Set(8, (int)value); }

    public uint Des { get => (uint)Get(12); set => Set(12, (int)value); }

    // MOUSEDATA at 16 (Core/MOUSEDATA.cs).
    public int X { get => Get(16); set => Set(16, value); }

    public int Y { get => Get(20); set => Set(20, value); }

    public int WheelDelta { get => Get(24); set => Set(24, value); }

    public int MouseFlags { get => Get(28); set => Set(28, value); }

    // KEYBDDATA at 24 (Core/KEYBDDATA.cs), after an 8-byte DateTime at 16.
    public int Vk { get => Get(24); set => Set(24, value); }

    public int KeyFlags { get => Get(28); set => Set(28, value); }

    /// <summary>Machine1..4: the random words of a handshake, at 16, 20, 24, 28.</summary>
    public uint Machine(int i) => (uint)Get(16 + (4 * i));

    public void SetMachine(int i, uint value) => Set(16 + (4 * i), (int)value);

    /// <summary>ASCII, space-padded to 32 bytes, trimmed on reading (DATA.cs MachineName).</summary>
    public string Name
    {
        get => Encoding.ASCII.GetString(Bytes, 32, 32).Trim();
        set => Encoding.ASCII.GetBytes(value.PadRight(32, ' ')).AsSpan(0, 32).CopyTo(Bytes.AsSpan(32));
    }

    /// <summary>DATA.cs IsBigPackage.</summary>
    public bool IsBig => Type is PackageType.Hello or PackageType.Awake or PackageType.Heartbeat or PackageType.HeartbeatEx
        or PackageType.Handshake or PackageType.HandshakeAck or PackageType.ClipboardPush or PackageType.Clipboard
        or PackageType.ClipboardAsk or PackageType.ClipboardImage or PackageType.ClipboardText or PackageType.ClipboardDataEnd
        || ((byte)Type & (byte)PackageType.Matrix) == (byte)PackageType.Matrix;

    public int Length => IsBig ? Big : Small;

    private int Get(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(offset));

    private void Set(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(offset), value);
}

/// <summary>The shared security key, as MWB turns it into AES keys and a magic number.</summary>
public sealed class MwbKey
{
    /// <summary>
    /// The current format starts each stream with a 16-byte salt and a 16-byte IV in the clear (Encryption.cs:125-180 at
    /// 48c4bad07).
    /// </summary>
    public const int HeaderSize = 32;

    // Encryption.cs: InitialIV = ulong.MaxValue.ToString(). The PBKDF2 salt is its UTF-16 bytes, the IV its first 16 ASCII bytes.
    private const string InitialIV = "18446744073709551615";

    private readonly string key;
    private MwbKey? standalone;

    /// <summary>Whitespace is dropped, as MWB's setup page does (Form/Settings/SetupPage2a.cs:92).</summary>
    public MwbKey(string key)
        : this(string.Concat(key.Where(c => !char.IsWhiteSpace(c))), false)
    {
    }

    private MwbKey(string k, bool standalone)
    {
        if (k.Length < 16)
        {
            throw new ArgumentException("the key must have at least 16 characters (MWB's own rule, Encryption.cs IsKeyValid)", nameof(key));
        }

        key = k;
        IsStandalone = standalone;

        // Encryption.cs GenLegalKey at a9e1f0f20^: PBKDF2-SHA512, 50000 iterations, 32 bytes. Standalone MWB 2.2.1 uses
        // SHA-1 instead (established by testing against the installed program).
        AesKey = Rfc2898DeriveBytes.Pbkdf2(k, Encoding.Unicode.GetBytes(InitialIV), 50000, standalone ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA512, 32);
        Magic = Get24BitHash(k, standalone ? SHA256.HashData : SHA512.HashData);
    }

    /// <summary>
    /// The same key as standalone MWB 2.x (Microsoft Garage, 2.2.1.0327) derives it for the old format: its AES key is
    /// PBKDF2-SHA1 and its magic number SHA-256, where PowerToys uses SHA-512 for both (established by testing against the
    /// installed program). Everything else on the wire is the same. Derived on first use (about 50 ms).
    /// </summary>
    public MwbKey Standalone => IsStandalone ? this : standalone ??= new MwbKey(key, true);

    /// <summary>This key is derived as standalone MWB 2.x derives it.</summary>
    public bool IsStandalone { get; }

    public static byte[] Iv { get; } = Encoding.ASCII.GetBytes(InitialIV[..16]);

    internal byte[] AesKey { get; }

    /// <summary>Its top 16 bits go in bytes 2-3 of every package (SocketStuff.cs TcpSendData).</summary>
    public uint Magic { get; }

    /// <summary>
    /// AES-256-CBC, zero padding. Without a header, the old format's fixed key and IV (Encryption.cs InitEncryption,
    /// GetEncryptedStream at a9e1f0f20^). With one, the current format: the key is PBKDF2-SHA512 of the key with the
    /// header's salt, 100000 iterations (Encryption.cs:42, :115-123 at 48c4bad07), and the IV is the header's second half.
    /// </summary>
    public ICryptoTransform Encryptor(byte[]? header = null) =>
        header is null ? Cipher().CreateEncryptor(AesKey, Iv) : Cipher().CreateEncryptor(CurrentKey(header), header[16..HeaderSize]);

    public ICryptoTransform Decryptor(byte[]? header = null) =>
        header is null ? Cipher().CreateDecryptor(AesKey, Iv) : Cipher().CreateDecryptor(CurrentKey(header), header[16..HeaderSize]);

    /// <summary>
    /// Stamps the magic number and checksum into <paramref name="p"/> and returns the bytes to encrypt (SocketStuff.cs
    /// TcpSendData). The checksum covers bytes 2-31 only, even in a big package.
    /// </summary>
    public byte[] Seal(Package p)
    {
        byte[] bytes = p.Bytes.AsSpan(0, p.Length).ToArray();
        bytes[3] = (byte)((Magic >> 24) & 0xFF);
        bytes[2] = (byte)((Magic >> 16) & 0xFF);
        bytes[1] = Checksum(bytes);
        return bytes;
    }

    /// <summary>
    /// True when the first 32 bytes carry this key's magic and a good checksum (SocketStuff.cs ProcessReceivedDataEx).
    /// A package encrypted with another key fails this. Clears bytes 1-3 as MWB does.
    /// </summary>
    public bool Open(byte[] first32)
    {
        // MWB compares an int with a uint, so both widen to long: a key whose magic has the top bit set never matches,
        // in MWB as here.
        int magic = (first32[3] << 24) + (first32[2] << 16);
        bool ok = magic == (Magic & 0xFFFF0000) && first32[1] == Checksum(first32);
        first32[1] = first32[2] = first32[3] = 0;
        return ok;
    }

    private static byte Checksum(byte[] bytes)
    {
        byte sum = 0;
        for (int i = 2; i < Package.Small; i++)
        {
            sum += bytes[i];
        }

        return sum;
    }

    /// <summary>Encryption.cs Get24BitHash: SHA-512 (SHA-256 in standalone MWB) of the key's low bytes, rehashed 50000 times.</summary>
    private static uint Get24BitHash(string key, Func<byte[], byte[]> hashData)
    {
        byte[] bytes = new byte[Package.Small];
        for (int i = 0; i < bytes.Length && i < key.Length; i++)
        {
            bytes[i] = (byte)key[i];
        }

        byte[] hash = hashData(bytes);
        for (int i = 0; i < 50000; i++)
        {
            hash = hashData(hash);
        }

        return unchecked((uint)((hash[0] << 23) + (hash[1] << 16) + (hash[^1] << 8) + hash[2]));
    }

    private byte[] CurrentKey(byte[] header) => Rfc2898DeriveBytes.Pbkdf2(key, header[..16], 100000, HashAlgorithmName.SHA512, 32);

    private static Aes Cipher()
    {
        Aes aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.Zeros;
        return aes;
    }
}
