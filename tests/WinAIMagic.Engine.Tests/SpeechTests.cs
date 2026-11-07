using System.Text;
using Concentus;
using Concentus.Enums;
using WinAIMagic.Core.Support;
using Xunit;

namespace WinAIMagic.Engine.Tests;

/// <summary>Speech on this PC: the sound the speech engine gets, when a spoken question is done, and where the engine runs.</summary>
public class SpeechTests : EngineTest
{
    private static SystemInfo Pc(int vram, ulong? free = null) =>
        SystemInfo.Simulated(vram == 0 ? FakeGpuBackend.Cpu() : FakeGpuBackend.Nvidia(vram), 32, 8, free);

    /// <summary>The model takes about 1 GB on a card: a card of 8 or 16 GB holds it, unless a loaded model leaves too little free.</summary>
    [Fact]
    public void TheCardOnlyWithRoom()
    {
        Assert.True(SpeechPlanner.OnCard(Pc(16)));
        Assert.True(SpeechPlanner.OnCard(Pc(8)));
        Assert.False(SpeechPlanner.OnCard(Pc(0)));
        Assert.False(SpeechPlanner.OnCard(Pc(16, free: 1UL << 30)));
        Assert.True(SpeechPlanner.OnCard(Pc(16, free: 4UL << 30)));
    }

    [Fact]
    public void TheProcessorIsSaid()
    {
        var card = SpeechPlanner.Arguments("m.bin", "in.wav", "out", "nl", true, 8);
        Assert.DoesNotContain("-ng", card);
        Assert.Equal("nl", card[card.ToList().IndexOf("-l") + 1]);
        Assert.Contains("-ng", SpeechPlanner.Arguments("m.bin", "in.wav", "out", "nl", false, 8));
    }

    /// <summary>On the processor the model listens to the recording and 2 s more, not to 30 s; on a card, and from 28 s on, to all of it.</summary>
    [Fact]
    public void AShortQuestionAShortWindow()
    {
        Assert.Equal(384, SpeechPlanner.Window(5.6, onCard: false));
        Assert.Equal(128, SpeechPlanner.Window(0.3, onCard: false));
        Assert.Null(SpeechPlanner.Window(5.6, onCard: true));
        Assert.Null(SpeechPlanner.Window(40, onCard: false));
        var args = SpeechPlanner.Arguments("m.bin", "in.wav", "out", "nl", false, 8, 5.6).ToList();
        Assert.Equal("384", args[args.IndexOf("-ac") + 1]);
        Assert.DoesNotContain("-ac", SpeechPlanner.Arguments("m.bin", "in.wav", "out", "nl", true, 8, 5.6));
    }

    [Fact]
    public void WhatIsNoSpeechGoes()
    {
        Assert.Equal("Hallo daar. Hoe gaat het?", SpeechPlanner.Clean(" [BLANK_AUDIO]\n Hallo daar.\n (muziek) Hoe gaat het?\n"));
        Assert.Equal("", SpeechPlanner.Clean("[BLANK_AUDIO]\n"));
    }

    private static bool Run(SpeechPause p, double level, double seconds)
    {
        bool done = false;
        for (int i = 0, n = (int)Math.Round(seconds * 10); i < n; i++) done |= p.Add(level, TimeSpan.FromMilliseconds(100));
        return done;
    }

    /// <summary>Quiet, speaking, then two seconds of quiet: the question is done, and not a moment before.</summary>
    [Fact]
    public void TwoSecondsAfterSpeaking()
    {
        var p = new SpeechPause();
        Assert.False(Run(p, 0.002, 1));
        Assert.False(Run(p, 0.1, 1.5));
        Assert.True(p.HeardSpeech);
        Assert.False(Run(p, 0.002, 1.8));
        Assert.True(Run(p, 0.002, 0.3));
    }

    /// <summary>A fan or a hum is the room, not speech; a click is too short to be a word.</summary>
    [Fact]
    public void AHumOrAClickIsNoQuestion()
    {
        var p = new SpeechPause();
        Assert.False(Run(p, 0.03, 10));
        Assert.False(p.HeardSpeech);
        Assert.False(Run(p, 0.5, 0.1));
        Assert.False(Run(p, 0.03, 5));
        Assert.False(p.HeardSpeech);
    }

    /// <summary>A pause in the middle of a sentence is no end.</summary>
    [Fact]
    public void APauseInASentenceIsNoEnd()
    {
        var p = new SpeechPause();
        Assert.False(Run(p, 0.1, 1));
        Assert.False(Run(p, 0.003, 1.5));
        Assert.False(Run(p, 0.1, 1));
        Assert.True(Run(p, 0.003, 2));
    }

    // Telegram voice messages

    /// <summary>An Ogg page per packet, as the encoder on a phone writes them (the checksum stays 0: the reader does not check it).</summary>
    internal static byte[] OggOpus(short[] samples, int channels = 1)
    {
        var out_ = new MemoryStream();
        int seq = 0;
        void Page(byte[] packet, byte type, long granule)
        {
            var lacing = new List<byte>();
            int n = packet.Length;
            while (n >= 255) { lacing.Add(255); n -= 255; }
            lacing.Add((byte)n);
            var w = new BinaryWriter(out_);
            w.Write("OggS"u8); w.Write((byte)0); w.Write(type); w.Write(granule); w.Write(1234); w.Write(seq++); w.Write(0);
            w.Write((byte)lacing.Count); w.Write(lacing.ToArray()); w.Write(packet);
        }
        var head = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
        head[8] = 1; head[9] = (byte)channels;
        BitConverter.GetBytes((ushort)312).CopyTo(head, 10);
        BitConverter.GetBytes(48000).CopyTo(head, 12);
        Page(head, 2, 0);
        Page(Encoding.ASCII.GetBytes("OpusTags\0\0\0\0\0\0\0\0"), 0, 0);
        var enc = OpusCodecFactory.CreateEncoder(16000, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        int frame = 320 * channels;
        var buffer = new byte[4000];
        long granule = 0;
        for (int at = 0; at + frame <= samples.Length; at += frame)
        {
            int len = enc.Encode(samples.AsSpan(at, frame), 320, buffer, buffer.Length);
            granule += 960;
            Page(buffer[..len], 0, granule);
        }
        return out_.ToArray();
    }

    private static short[] Tone(double seconds, double amplitude = 0.3, int channels = 1)
    {
        int n = (int)(seconds * 16000);
        var s = new short[n * channels];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < channels; c++) s[i * channels + c] = (short)(Math.Sin(2 * Math.PI * 440 * i / 16000.0) * amplitude * 32767);
        return s;
    }

    [Fact]
    public void AVoiceMessageBecomesSound()
    {
        var back = Audio.ReadOggOpus(OggOpus(Tone(2)));
        // the pre-skip goes: 312 at 48 kHz is 104 here
        Assert.InRange(back.Length, 32000 - 104 - 320, 32000);
        Assert.InRange(Audio.Level(back.AsSpan(1600, 16000)), 0.18, 0.25);
    }

    [Fact]
    public void AStereoMessageBecomesOneChannel()
    {
        var back = Audio.ReadOggOpus(OggOpus(Tone(1, channels: 2), channels: 2));
        Assert.InRange(back.Length, 16000 - 500, 16000);
        Assert.InRange(Audio.Level(back.AsSpan(1600, 8000)), 0.15, 0.25);
    }

    [Fact]
    public void AnotherFileIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => Audio.ReadOggOpus("RIFF....WAVEfmt "u8.ToArray().Concat(new byte[40]).ToArray()));
    }

    [Fact]
    public void AWavFileAsTheEngineReadsIt()
    {
        var path = Path.Combine(Root, "a.wav");
        Directory.CreateDirectory(Root);
        Audio.WriteWav(path, new short[] { 1, -2, 3 });
        var b = File.ReadAllBytes(path);
        Assert.Equal(44 + 6, b.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(b, 0, 4));
        Assert.Equal(16000, BitConverter.ToInt32(b, 24));
        Assert.Equal(-2, BitConverter.ToInt16(b, 46));
    }

    // the engine

    private string Model(string how)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "speech.bin");
        File.WriteAllText(path, how);
        return path;
    }

    private static SpeechEngine Engine() => new() { ExecutableOverride = FakeExe };

    [Fact]
    public async Task SpeechBecomesText()
    {
        var (text, error) = await Engine().Transcribe(Tone(1), Model("goed"), "nl", Pc(16), FakeGpuBackend.Nvidia(16));
        Assert.Null(error);
        Assert.Equal("hallo in nl op de kaart, 16000 samples", text);
    }

    [Fact]
    public async Task WithoutACardOnTheProcessor()
    {
        var (text, _) = await Engine().Transcribe(Tone(1), Model("goed"), "nl", Pc(0), FakeGpuBackend.Cpu());
        Assert.EndsWith("op de processor, 16000 samples", text);
    }

    /// <summary>A card that refuses: the processor does it.</summary>
    [Fact]
    public async Task ACardThatRefusesLeavesItToTheProcessor()
    {
        var (text, error) = await Engine().Transcribe(Tone(1), Model("kaart stuk"), "nl", Pc(16), FakeGpuBackend.Nvidia(16));
        Assert.Null(error);
        Assert.Contains("op de processor", text);
    }

    [Fact]
    public async Task AFailureSaysWhy()
    {
        var (text, error) = await Engine().Transcribe(Tone(1), Model("kapot"), "nl", Pc(16), FakeGpuBackend.Nvidia(16));
        Assert.Null(text);
        Assert.StartsWith("De spraakherkenning lukte niet.", error);
        Assert.Contains("failed to allocate", error);
        Assert.True(File.Exists(Paths.SpeechLog));
    }

    [Fact]
    public async Task SilenceIsNotUnderstood()
    {
        var (text, error) = await Engine().Transcribe(Tone(1), Model("stil"), "nl", Pc(16), FakeGpuBackend.Nvidia(16));
        Assert.Null(text);
        Assert.Equal("Er werd niets verstaan.", error);
    }

    [Fact]
    public async Task WhatIsMissingIsSaid()
    {
        Assert.Equal("De opname was te kort.", (await Engine().Transcribe(Tone(0.1), Model("goed"), "nl", Pc(16), FakeGpuBackend.Nvidia(16))).Error);
        Assert.Contains("Installeer WinAIMagic opnieuw", (await new SpeechEngine { ExecutableOverride = Path.Combine(Root, "weg.exe") }
            .Transcribe(Tone(1), Model("goed"), "nl", Pc(16), FakeGpuBackend.Nvidia(16))).Error);
        Assert.Contains("nog niet gedownload", (await Engine().Transcribe(Tone(1), Path.Combine(Root, "weg.bin"), "nl", Pc(16), FakeGpuBackend.Nvidia(16))).Error);
    }

    [Fact]
    public async Task AVoiceMessageFileBecomesText()
    {
        var ogg = Path.Combine(Root, "stem.ogg");
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(ogg, OggOpus(Tone(2)));
        var (text, error) = await Engine().TranscribeFile(ogg, Model("goed"), "auto", Pc(16), FakeGpuBackend.Nvidia(16));
        Assert.Null(error);
        Assert.StartsWith("hallo in auto op de kaart", text);
        File.WriteAllText(ogg, "geen geluid");
        Assert.StartsWith("Het geluid kon niet worden gelezen", (await Engine().TranscribeFile(ogg, Model("goed"), "nl", Pc(16), FakeGpuBackend.Nvidia(16))).Error);
    }

    [Fact]
    public void TheSpeechModelIsNoChatModel()
    {
        var c = ModelCatalog.Shared;
        var m = Assert.Single(c.Speech);
        Assert.DoesNotContain(m, c.All);
        Assert.Null(c.Model(m.Id));
        Assert.Equal(m, c.Find(m.Id));
        Assert.Contains("/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/", m.Files[0].Url);
    }
}
