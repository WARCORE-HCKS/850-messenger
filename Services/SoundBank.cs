using System.IO;
using System.Media;

namespace Area850.Services;

public static class SoundBank
{
    public static void Message() => Play(Blip(880, 70), Blip(1320, 90));
    public static void Nudge() => Play(Blip(420, 80), Blip(280, 120), Blip(420, 80));
    public static void SignOn() => Play(Blip(660, 80), Blip(880, 120));
    public static void Call() => Play(Blip(520, 160), Blip(780, 160));
    public static void Online() => Play(Blip(784, 90), Blip(1046, 140));
    public static void Auth() => Play(Blip(660, 80), Blip(880, 80), Blip(1108, 120));

    private static void Play(params byte[][] clips)
    {
        Task.Run(() =>
        {
            foreach (var c in clips)
            {
                using var ms = new MemoryStream(c);
                using var p = new SoundPlayer(ms);
                p.PlaySync();
            }
        });
    }

    private static byte[] Blip(int hz, int ms)
    {
        const int rate = 22050;
        int n = rate * ms / 1000;
        var data = new byte[44 + n * 2];
        void W(int o, int v, int b)
        {
            for (int i = 0; i < b; i++) data[o + i] = (byte)((v >> (8 * i)) & 0xff);
        }
        EncodingHeader(data, n * 2);
        for (int i = 0; i < n; i++)
        {
            double env = Math.Sin(Math.PI * i / n);
            short s = (short)(Math.Sin(2 * Math.PI * hz * i / rate) * 9000 * env);
            data[44 + i * 2] = (byte)(s & 0xff);
            data[45 + i * 2] = (byte)((s >> 8) & 0xff);
        }
        return data;
    }

    private static void EncodingHeader(byte[] d, int dataLen)
    {
        void S(int o, string t) { for (int i = 0; i < t.Length; i++) d[o + i] = (byte)t[i]; }
        void W(int o, int v, int b) { for (int i = 0; i < b; i++) d[o + i] = (byte)((v >> (8 * i)) & 0xff); }
        S(0, "RIFF"); W(4, 36 + dataLen, 4); S(8, "WAVE"); S(12, "fmt "); W(16, 16, 4);
        W(20, 1, 2); W(22, 1, 2); W(24, 22050, 4); W(28, 44100, 4); W(32, 2, 2); W(34, 16, 2);
        S(36, "data"); W(40, dataLen, 4);
    }
}
