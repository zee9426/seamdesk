using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Seamdesk.Core;
using Seamdesk.Transport;
using Microsoft.Extensions.Time.Testing;

namespace Seamdesk.Mwb.Tests;

/// <summary>
/// Against vectors.json (the old format, MWB at a9e1f0f20^), vectors-current.json (0.101 and later, MWB at 48c4bad07) and
/// any other vectors*.json beside them: streams captured between Seamdesk and MWB's own code (tools/mwb-vectors), where
/// MWB's code produced or decoded every package. If Seamdesk's codec changes, these say whether it still matches.
/// </summary>
public class VectorsTests
{
    /// <summary>Both directions of every vectors*.json next to the tests.</summary>
    public static TheoryData<string, string> Streams()
    {
        TheoryData<string, string> rows = [];
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory, "vectors*.json").Select(f => Path.GetFileName(f)).Order())
        {
            rows.Add(file, "mwbToSeamdesk");
            rows.Add(file, "seamdeskToMwb");
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void Seamdesk_decrypts_encrypts_and_stamps_the_captured_stream_exactly_as_MWB(string file, string direction)
    {
        (JsonElement vectors, MwbKey key, MwbFormat format) = Load(file);
        byte[] cipher = Convert.FromHexString(vectors.GetProperty(direction).GetProperty("cipher").GetString()!);
        byte[] plain = Convert.FromHexString(vectors.GetProperty(direction).GetProperty("plain").GetString()!);

        // The current format starts with its salt and IV in the clear.
        byte[]? header = format == MwbFormat.Current ? cipher[..MwbKey.HeaderSize] : null;
        cipher = cipher[(header?.Length ?? 0)..];
        Assert.Equal(plain, Transform(key.Decryptor(header), cipher));
        Assert.Equal(cipher, Transform(key.Encryptor(header), plain));
        int count = 0;
        for (int at = 16; at < plain.Length; count++)
        {
            byte[] bytes = plain[at..Math.Min(at + Package.Big, plain.Length)];
            Package package = new(bytes);
            Assert.Equal(plain[at..(at + package.Length)], key.Seal(package)); // magic and checksum
            at += package.Length;
        }

        Assert.True(count > 40, $"{count} packages");
    }

    [Theory]
    [InlineData("vectors.json")]
    [InlineData("vectors-current.json")]
    public async Task Input_frames_become_the_packages_MWB_decoded(string file)
    {
        (JsonElement vectors, _, MwbFormat format) = Load(file);
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener, format: format);
        await using (node)
        await using (fake)
        {
            foreach (JsonElement input in vectors.GetProperty("inputs").EnumerateArray())
            {
                await link.SendAsync(Parse(input.GetProperty("frame").GetString()!));
                AssertSame(input.GetProperty("package"), await fake.NextAsync());
            }
        }
    }

    [Theory]
    [InlineData("vectors.json")]
    [InlineData("vectors-current.json")]
    public async Task Packages_from_an_MWB_controller_become_the_frames_it_meant_and_the_hand_back_is_what_MWB_decoded(string file)
    {
        (JsonElement vectors, _, MwbFormat format) = Load(file);
        using var listener = Kit.Listen();
        (MwbNode node, FakeMwb fake, MwbLink link) = await Kit.ConnectedAsync(listener, Kit.Controller, format);
        await using (node)
        await using (fake)
        {
            Assert.IsType<Monitors>(await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
            foreach (JsonElement step in vectors.GetProperty("driven").EnumerateArray())
            {
                await fake.SendAsync(new Package(Convert.FromHexString(step.GetProperty("package").GetString()!)));
                foreach (JsonElement frame in step.GetProperty("frames").EnumerateArray())
                {
                    Assert.Equal(Parse(frame.GetString()!), await link.Incoming.ReadAsync().AsTask().WaitAsync(Kit.Patience));
                }
            }

            await link.SendAsync(new Handoff("peer", new Point(1000, 0)));
            AssertSame(vectors.GetProperty("handBack"), await fake.NextAsync());
        }
    }

    internal static (JsonElement Vectors, MwbKey Key, MwbFormat Format) Load(string file)
    {
        JsonElement v = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file))).RootElement;
        MwbKey key = new(v.GetProperty("key").GetString()!);
        return (v, v.TryGetProperty("keyDerivation", out JsonElement d) && d.GetString() == "standalone" ? key.Standalone : key,
            Enum.Parse<MwbFormat>(v.GetProperty("format").GetString()!));
    }

    /// <summary>Bytes 1-3 (checksum, magic) are cleared on receipt and 4-7 are the sender's running id.</summary>
    private static void AssertSame(JsonElement hex, Package sent)
    {
        byte[] expected = Convert.FromHexString(hex.GetString()!);
        Assert.Equal(expected[..1], sent.Bytes[..1]);
        Assert.Equal(expected[8..], sent.Bytes[8..expected.Length]);
    }

    private static byte[] Transform(ICryptoTransform transform, byte[] input)
    {
        byte[] output = new byte[input.Length];
        transform.TransformBlock(input, 0, input.Length, output, 0);
        return output;
    }

    /// <summary>The harness's names for the frames it sent, for example "PointerButton X1 down" or "Key 0x25 up extended".</summary>
    private static Frame Parse(string text)
    {
        string[] w = text.Split(' ');
        return w[0] switch
        {
            "Handoff" => new Handoff(w[1], new Point(int.Parse(w[2]), int.Parse(w[3]))),
            "PointerMove" => new PointerMove(int.Parse(w[1]), int.Parse(w[2])),
            "PointerButton" => new PointerButton(Enum.Parse<MouseButton>(w[1]), w[2] == "down"),
            "Wheel" => new Wheel(int.Parse(w[1]), w[2] == "horizontal"),
            "Key" => new Key(Convert.ToUInt16(w[1], 16), 0, w[2] == "down", w.Length > 3),
            _ => throw new FormatException(text),
        };
    }
}
