using System.Runtime.InteropServices;

namespace OmenGamingShell;

public static class NavigationSound
{
    private static byte[]? _activeSound;

    public static void Play(ControllerCommand command, int volume)
    {
        var (frequency, duration) = command switch
        {
            ControllerCommand.Accept => (720, 65),
            ControllerCommand.Back => (360, 75),
            _ => (520, 35)
        };
        _activeSound = CreateWave(frequency, duration, Math.Clamp(volume, 0, 100));
        PlaySound(_activeSound, IntPtr.Zero, 0x0002 | 0x0004);
    }

    private static byte[] CreateWave(int frequency, int milliseconds, int volume)
    {
        const int sampleRate = 22050;
        var samples = sampleRate * milliseconds / 1000;
        var dataSize = samples * 2;
        var wave = new byte[44 + dataSize];
        void Write(int offset, int value) => BitConverter.GetBytes(value).CopyTo(wave, offset);
        void WriteShort(int offset, short value) => BitConverter.GetBytes(value).CopyTo(wave, offset);
        "RIFF"u8.CopyTo(wave); Write(4, 36 + dataSize); "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        Write(16, 16); WriteShort(20, 1); WriteShort(22, 1); Write(24, sampleRate); Write(28, sampleRate * 2);
        WriteShort(32, 2); WriteShort(34, 16); "data"u8.CopyTo(wave.AsSpan(36)); Write(40, dataSize);
        var amplitude = short.MaxValue * volume / 100d * 0.18;
        for (var i = 0; i < samples; i++)
        {
            var fade = 1d - (double)i / samples;
            var value = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * amplitude * fade);
            WriteShort(44 + i * 2, value);
        }
        return wave;
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);
}
