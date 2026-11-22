using Omega.Plugins;
namespace Disgaea.Formats;
internal readonly record struct NitroNote(byte Type, ushort Wave, ushort Archive, byte Key, byte Attack, byte Decay, byte Sustain, byte Release, byte Pan);
internal sealed class NitroBank
{
    private const int TableAt = 0x3C;
    private const int NoteSize = 10;
    private readonly (byte Type, byte Low, byte[] Highs, NitroNote[] Notes)[] _programs;
    private NitroBank((byte, byte, byte[], NitroNote[])[] programs)
    {
        _programs = programs;
    }
    public static NitroBank Read(ReadOnlySpan<byte> data)
    {
        BinaryRead.Need(data, 0, TableAt);
        if (!data.StartsWith("SBNK"u8))
        {
            throw new InvalidDataException("not an SBNK bank");
        }
        uint count = BinaryRead.U32(data, 0x38);
        BinaryRead.Need(data, TableAt, (int)Math.Min(int.MaxValue, count * 4L));
        (byte, byte, byte[], NitroNote[])[] programs = new (byte, byte, byte[], NitroNote[])[count];
        for (int i = 0; i < count; i++)
        {
            byte type = data[TableAt + (4 * i)];
            int at = BinaryRead.U16(data, TableAt + (4 * i) + 1);
            programs[i] = type switch
            {
                0 => (0, 0, [], []),
                16 => Drums(data, at),
                17 => Split(data, at),
                _ => (type, 0, [], [Note(data, at, type)]),
            };
        }
        return new NitroBank(programs);
    }
    public NitroNote? Find(int program, int key)
    {
        if ((uint)program >= (uint)_programs.Length)
        {
            return null;
        }
        (byte type, byte low, byte[] highs, NitroNote[] notes) = _programs[program];
        switch (type)
        {
            case 0:
                return null;
            case 16:
                return key < low || key - low >= notes.Length ? null : notes[key - low];
            case 17:
                for (int i = 0; i < notes.Length; i++)
                {
                    if (key <= highs[i])
                    {
                        return notes[i];
                    }
                }
                return null;
            default:
                return notes[0];
        }
    }
    private static (byte, byte, byte[], NitroNote[]) Drums(ReadOnlySpan<byte> data, int at)
    {
        BinaryRead.Need(data, at, 2);
        byte low = data[at];
        int count = data[at + 1] - low + 1;
        NitroNote[] notes = new NitroNote[Math.Max(0, count)];
        for (int i = 0; i < notes.Length; i++)
        {
            int entry = at + 2 + (i * (NoteSize + 2));
            BinaryRead.Need(data, entry, 1);
            notes[i] = Note(data, entry + 2, data[entry]);
        }
        return (16, low, [], notes);
    }
    private static (byte, byte, byte[], NitroNote[]) Split(ReadOnlySpan<byte> data, int at)
    {
        BinaryRead.Need(data, at, 8);
        int count = 0;
        while (count < 8 && data[at + count] != 0)
        {
            count++;
        }
        byte[] highs = data.Slice(at, count).ToArray();
        NitroNote[] notes = new NitroNote[count];
        for (int i = 0; i < count; i++)
        {
            int entry = at + 8 + (i * (NoteSize + 2));
            BinaryRead.Need(data, entry, 1);
            notes[i] = Note(data, entry + 2, data[entry]);
        }
        return (17, 0, highs, notes);
    }
    private static NitroNote Note(ReadOnlySpan<byte> data, int at, byte type)
    {
        BinaryRead.Need(data, at, NoteSize);
        return new NitroNote(type, BinaryRead.U16(data, at), BinaryRead.U16(data, at + 2), data[at + 4], data[at + 5], data[at + 6], data[at + 7], data[at + 8], data[at + 9]);
    }
}
