using DWSSK.Formats.Core;
namespace DWSSK.Formats.Audio;
internal abstract class HircCodec(bool big)
{
    public bool Big { get; } = big;
    public abstract long Val(JObj d, string k, char fmt);
    public abstract long Count(JObj d, string k, char fmt);
    public abstract List<object?> Items(JObj d, string k, long n, Action<HircCodec, JObj> fn);
    public abstract object? Prop(JObj d, string k);
    public abstract long Enum(JObj d, string k, char fmt, IReadOnlyDictionary<long, string> table);
}
internal sealed class HircReader(byte[] b, bool big) : HircCodec(big)
{
    public byte[] B { get; } = b;
    public int O { get; set; }
    public bool Done => O == B.Length;
    private static int Size(char fmt)
    {
        return fmt switch
        {
            'B' => 1,
            'H' or 'h' => 2,
            'I' or 'i' or 'f' => 4,
            'd' => 8,
            _ => throw new StructException($"bad format char '{fmt}'"),
        };
    }
    public object Get(char fmt)
    {
        int n = Size(fmt);
        if (O + n > B.Length)
        {
            throw new StructException($"unpack_from requires a buffer of at least {O + n} bytes");
        }
        object v = fmt switch
        {
            'B' => (object)(long)B[O],
            'H' => (long)Bin.U16(B, O, Big),
            'h' => (long)Bin.I16(B, O, Big),
            'I' => (long)Bin.U32(B, O, Big),
            'i' => (long)Bin.I32(B, O, Big),
            'f' => (double)Bin.F32(B, O, Big),
            _ => Bin.F64(B, O, Big),
        };
        O += n;
        return v;
    }
    public long GetLong(char fmt)
    {
        return J.L(Get(fmt));
    }
    public override long Val(JObj d, string k, char fmt)
    {
        object v = Get(fmt);
        d[k] = v;
        return v is long l ? l : 0;
    }
    public override long Count(JObj d, string k, char fmt)
    {
        return GetLong(fmt);
    }
    public override List<object?> Items(JObj d, string k, long n, Action<HircCodec, JObj> fn)
    {
        List<object?> list = [];
        d[k] = list;
        for (long i = 0; i < n; i++)
        {
            JObj x = [];
            fn(this, x);
            list.Add(x);
        }
        return list;
    }
    public override object? Prop(JObj d, string k)
    {
        long raw = GetLong('I');
        float f = BitConverter.UInt32BitsToSingle((uint)raw);
        double af = Math.Abs((double)f);
        object v = raw == 0 || (af > 1e-6 && af < 1e7) ? (object)(double)f : raw;
        d[k] = v;
        return v;
    }
    public override long Enum(JObj d, string k, char fmt, IReadOnlyDictionary<long, string> table)
    {
        long v = GetLong(fmt);
        d[k] = table.TryGetValue(v, out string? name) ? name : v;
        return v;
    }
}
internal sealed class HircWriter(bool big) : HircCodec(big)
{
    public ByteWriter Out { get; } = new();
    public void Put(char fmt, object? v)
    {
        switch (fmt)
        {
            case 'B':
                Out.Add(J.U8(v));
                break;
            case 'H':
                Out.U16(J.U16(v), Big);
                break;
            case 'h':
                Out.U16((ushort)J.I16(v), Big);
                break;
            case 'I':
                Out.U32(J.U(v), Big);
                break;
            case 'i':
                Out.I32(J.I32(v), Big);
                break;
            case 'f':
                Out.F32(J.D(v), Big);
                break;
            case 'd':
                Out.F64(J.D(v), Big);
                break;
            default:
                throw new StructException($"bad format char '{fmt}'");
        }
    }
    public override long Val(JObj d, string k, char fmt)
    {
        object? v = d[k];
        Put(fmt, v);
        return v is long or bool or int ? J.L(v) : 0;
    }
    public override long Count(JObj d, string k, char fmt)
    {
        long n = J.A(d[k]).Count;
        Put(fmt, n);
        return n;
    }
    public override List<object?> Items(JObj d, string k, long n, Action<HircCodec, JObj> fn)
    {
        List<object?> list = J.A(d[k]);
        foreach (object? x in list)
        {
            fn(this, J.O(x));
        }
        return list;
    }
    public override object? Prop(JObj d, string k)
    {
        object? v = d[k];
        Put(v is double ? 'f' : 'I', v);
        return v;
    }
    public override long Enum(JObj d, string k, char fmt, IReadOnlyDictionary<long, string> table)
    {
        object? v = d[k];
        long value;
        if (v is string s)
        {
            value = table.FirstOrDefault(kv => kv.Value == s) is { Value: not null } hit
                ? hit.Key
                : throw new InvalidOperationException($"'{s}' is not a name in the table");
        }
        else
        {
            value = J.L(v);
        }
        Put(fmt, value);
        return value;
    }
}
internal static class Hirc
{
    public static readonly Dictionary<long, string> Codecs = new()
    {
        [0x10001] = "PCM", [0x20001] = "IMA ADPCM", [0x30001] = "XMA2", [0x40001] = "Vorbis", [0x50001] = "DSP ADPCM",
    };
    private static readonly Dictionary<long, string> s_props = Table(
        "Volume", "LFE", "Pitch", "LPF", "Priority", "PriorityDistanceOffset", "Loop", "FeedbackVolume", "FeedbackLPF",
        "MuteRatio", "PAN_LR", "PAN_FR", "CenterPCT", "DelayTime", "TransitionTime", "Probability");
    private static readonly Dictionary<long, string> s_interp = Table(
        "Log3", "Sine", "Log1", "InvSCurve", "Linear", "SCurve", "Exp1", "SineRecip", "Exp3", "Constant");
    private static readonly Dictionary<long, string> s_scaling = Table("None", "db_255", "dB_96_3", "Frequency_20_20000", "dB_96_3_NoCheck");
    private static readonly Dictionary<long, string> s_rtpcParams = new()
    {
        [0] = "Volume", [1] = "LFE", [2] = "Pitch", [3] = "LPF", [8] = "Priority", [9] = "MaxNumInstances",
        [10] = "Positioning_Radius_LPF", [11] = "Positioning_Divergence_Center_PCT",
        [12] = "Positioning_Cone_Attenuation_ON_OFF", [13] = "Positioning_Cone_Attenuation", [14] = "Positioning_Cone_LPF",
        [20] = "Position_PAN_RL", [21] = "Position_PAN_FR", [22] = "Position_Radius_SIM_ON_OFF",
        [23] = "Position_Radius_SIM_Attenuation", [24] = "BypassFX0", [25] = "BypassFX1", [26] = "BypassFX2",
        [27] = "BypassFX3", [28] = "BypassAllFX", [29] = "FeedbackVolume", [30] = "FeedbackLowpass", [31] = "FeedbackPitch",
    };
    private static readonly Dictionary<long, string> s_actions = new()
    {
        [0x0000] = "None", [0x1204] = "SetState", [0x1A02] = "BypassFX_M", [0x1A03] = "BypassFX_O", [0x1B02] = "ResetBypassFX_M",
        [0x1B03] = "ResetBypassFX_O", [0x1B04] = "ResetBypassFX_ALL", [0x1B05] = "ResetBypassFX_ALL_O",
        [0x1B08] = "ResetBypassFX_AE", [0x1B09] = "ResetBypassFX_AE_O", [0x1901] = "SetSwitch", [0x1002] = "UseState_E",
        [0x1102] = "UnuseState_E", [0x0403] = "Play", [0x0503] = "PlayAndContinue", [0x0102] = "Stop_E", [0x0103] = "Stop_E_O",
        [0x0104] = "Stop_ALL", [0x0105] = "Stop_ALL_O", [0x0108] = "Stop_AE", [0x0109] = "Stop_AE_O", [0x0202] = "Pause_E",
        [0x0203] = "Pause_E_O", [0x0204] = "Pause_ALL", [0x0205] = "Pause_ALL_O", [0x0208] = "Pause_AE", [0x0209] = "Pause_AE_O",
        [0x0302] = "Resume_E", [0x0303] = "Resume_E_O", [0x0304] = "Resume_ALL", [0x0305] = "Resume_ALL_O", [0x0308] = "Resume_AE",
        [0x0309] = "Resume_AE_O", [0x1C02] = "Break_E", [0x1C03] = "Break_E_O", [0x0602] = "Mute_M", [0x0603] = "Mute_O",
        [0x0702] = "Unmute_M", [0x0703] = "Unmute_O", [0x0704] = "Unmute_ALL", [0x0705] = "Unmute_ALL_O", [0x0708] = "Unmute_AE",
        [0x0709] = "Unmute_AE_O", [0x0A02] = "SetVolume_M", [0x0A03] = "SetVolume_O", [0x0B02] = "ResetVolume_M",
        [0x0B03] = "ResetVolume_O", [0x0B04] = "ResetVolume_ALL", [0x0B05] = "ResetVolume_ALL_O", [0x0B08] = "ResetVolume_AE",
        [0x0B09] = "ResetVolume_AE_O", [0x0802] = "SetPitch_M", [0x0803] = "SetPitch_O", [0x0902] = "ResetPitch_M",
        [0x0903] = "ResetPitch_O", [0x0904] = "ResetPitch_ALL", [0x0905] = "ResetPitch_ALL_O", [0x0908] = "ResetPitch_AE",
        [0x0909] = "ResetPitch_AE_O", [0x0E02] = "SetLPF_M", [0x0E03] = "SetLPF_O", [0x0F02] = "ResetLPF_M",
        [0x0F03] = "ResetLPF_O", [0x0F04] = "ResetLPF_ALL", [0x0F05] = "ResetLPF_ALL_O", [0x0F08] = "ResetLPF_AE",
        [0x0F09] = "ResetLPF_AE_O", [0x0C02] = "SetBusVolume_M", [0x0C03] = "SetBusVolume_O", [0x0D02] = "ResetBusVolume_M",
        [0x0D03] = "ResetBusVolume_O", [0x0D04] = "ResetBusVolume_ALL", [0x0D08] = "ResetBusVolume_AE", [0x2103] = "PlayEvent",
        [0x1511] = "StopEvent", [0x1611] = "PauseEvent", [0x1711] = "ResumeEvent", [0x1820] = "Duck", [0x1D00] = "Trigger",
        [0x1D01] = "Trigger_O", [0x1E02] = "Seek_E", [0x1E03] = "Seek_E_O", [0x1E04] = "Seek_ALL", [0x1E05] = "Seek_ALL_O",
        [0x1E08] = "Seek_AE", [0x1E09] = "Seek_AE_O", [0x2202] = "ResetPlaylist_E", [0x2203] = "ResetPlaylist_E_O",
        [0x1302] = "SetGameParameter", [0x1303] = "SetGameParameter_O", [0x1402] = "ResetGameParameter",
        [0x1403] = "ResetGameParameter_O", [0x1F02] = "Release", [0x1F03] = "Release_O",
    };
    public static readonly Dictionary<long, string> Types = new()
    {
        [1] = "settings", [2] = "sound", [3] = "action", [4] = "event", [5] = "random_sequence", [6] = "switch",
        [7] = "actor_mixer", [8] = "bus", [9] = "blend", [10] = "music_segment", [11] = "music_track",
        [12] = "music_switch", [13] = "music_playlist", [14] = "attenuation",
    };
    private static Dictionary<long, string> Table(params string[] names)
    {
        return names.Select((n, i) => (n, (long)i)).ToDictionary(t => t.Item2, t => t.n);
    }
    private static void Vals(HircCodec c, JObj d, params (string K, char F)[] fields)
    {
        foreach ((string k, char f) in fields)
        {
            c.Val(d, k, f);
        }
    }
    private static void Ids(HircCodec c, JObj d, string k, char fmt = 'I')
    {
        c.Items(d, k, c.Count(d, k, fmt), (c2, x) => c2.Val(x, "id", 'I'));
    }
    private static void Bundle(HircCodec c, JObj d, string k, bool ranged = false)
    {
        long n = c.Count(d, k, 'B');
        List<object?> items = c.Items(d, k, n, (c2, x) => c2.Enum(x, "id", 'B', s_props));
        foreach (object? xo in items)
        {
            JObj x = J.O(xo);
            if (ranged)
            {
                c.Prop(x, "min");
                c.Prop(x, "max");
            }
            else
            {
                c.Prop(x, "value");
            }
        }
    }
    private static void Graph(HircCodec c, JObj d, string k, char fmt)
    {
        c.Items(d, k, c.Count(d, k, fmt), (c2, x) =>
        {
            c2.Val(x, "from", 'f');
            c2.Val(x, "to", 'f');
            c2.Enum(x, "interp", 'I', s_interp);
        });
    }
    private static void Rtpcs(HircCodec c, JObj d)
    {
        c.Items(d, "rtpcs", c.Count(d, "rtpcs", 'H'), (c2, x) =>
        {
            c2.Val(x, "rtpc", 'I');
            c2.Enum(x, "param", 'I', s_rtpcParams);
            c2.Val(x, "curve", 'I');
            c2.Enum(x, "scaling", 'B', s_scaling);
            Graph(c2, x, "points", 'H');
        });
    }
    private static void States(HircCodec c, JObj d)
    {
        c.Items(d, "state_groups", c.Count(d, "state_groups", 'I'), (c2, x) =>
        {
            c2.Val(x, "group", 'I');
            c2.Val(x, "sync", 'B');
            c2.Items(x, "states", c2.Count(x, "states", 'H'), (c3, y) =>
            {
                c3.Val(y, "state", 'I');
                c3.Val(y, "instance", 'I');
            });
        });
    }
    private static void Fx(HircCodec c, JObj d)
    {
        long nfx = c.Count(d, "fx", 'B');
        if (nfx != 0)
        {
            c.Val(d, "fx_bypass", 'B');
        }
        c.Items(d, "fx", nfx, (c2, x) => Vals(c2, x, ("index", 'B'), ("fx", 'I'), ("share_set", 'B'), ("rendered", 'B')));
    }
    private static void NodeBase(HircCodec c, JObj d)
    {
        c.Val(d, "override_fx", 'B');
        Fx(c, d);
        c.Val(d, "override_bus", 'I');
        c.Val(d, "parent", 'I');
        c.Val(d, "priority_override_parent", 'B');
        c.Val(d, "priority_distance_factor", 'B');
        Bundle(c, d, "props");
        Bundle(c, d, "ranged_props", true);
        long bits = c.Val(d, "positioning", 'B');
        long has3d = 0;
        if ((bits & 1) != 0)
        {
            has3d = c.Val(d, "is_3d", 'B');
            if (has3d == 0)
            {
                c.Val(d, "panner", 'B');
            }
        }
        if ((bits & 1) != 0 && has3d != 0)
        {
            long kind = c.Val(d, "type_3d", 'I');
            c.Val(d, "attenuation", 'I');
            c.Val(d, "spatialized", 'B');
            if (kind == 3)
            {
                c.Val(d, "dynamic", 'B');
            }
            else if (kind == 2)
            {
                Vals(c, d, ("path_mode", 'I'), ("looping", 'B'), ("transition_time", 'i'), ("follow_orientation", 'B'));
                c.Items(d, "vertices", c.Count(d, "vertices", 'I'), (c2, x) => Vals(c2, x, ("x", 'f'), ("y", 'f'), ("z", 'f'), ("duration", 'i')));
                long n = c.Count(d, "playlist", 'I');
                List<object?> playlist = c.Items(d, "playlist", n, (c2, x) => Vals(c2, x, ("first", 'I'), ("count", 'I')));
                foreach (object? xo in playlist)
                {
                    JObj x = J.O(xo);
                    c.Val(x, "x_range", 'f');
                    c.Val(x, "y_range", 'f');
                }
            }
        }
        Vals(c, d, ("virtual_queue", 'B'), ("kill_newest", 'B'), ("use_virtual", 'B'), ("max_instances", 'H'),
            ("global_limit", 'B'), ("below_threshold", 'B'), ("max_instances_override", 'B'), ("virtual_override", 'B'));
        States(c, d);
        Rtpcs(c, d);
    }
    private static void Source(HircCodec c, JObj d)
    {
        c.Enum(d, "codec", 'I', Codecs);
        long stream = c.Val(d, "stream", 'I');
        c.Val(d, "source", 'I');
        c.Val(d, "file", 'I');
        if (stream != 1)
        {
            c.Val(d, "offset", 'I');
            c.Val(d, "size", 'I');
        }
        c.Val(d, "bits", 'B');
    }
    private static void Sound(HircCodec c, JObj d)
    {
        Source(c, d);
        NodeBase(c, d);
    }
    private static void Children(HircCodec c, JObj d)
    {
        Ids(c, d, "children");
    }
    private static void RandomSequence(HircCodec c, JObj d)
    {
        NodeBase(c, d);
        Vals(c, d, ("loop", 'H'), ("transition_time", 'f'), ("transition_min", 'f'), ("transition_max", 'f'),
            ("avoid_repeat", 'H'), ("transition_mode", 'B'), ("random_mode", 'B'), ("mode", 'B'),
            ("unused_weight", 'B'), ("reset_playlist", 'B'), ("restart_backward", 'B'), ("continuous", 'B'),
            ("global", 'B'));
        Children(c, d);
        c.Items(d, "playlist", c.Count(d, "playlist", 'H'), (c2, x) => Vals(c2, x, ("id", 'I'), ("weight", 'i')));
    }
    private static void Switch(HircCodec c, JObj d)
    {
        NodeBase(c, d);
        Vals(c, d, ("group_type", 'I'), ("group", 'I'), ("default", 'I'), ("continuous", 'B'));
        Children(c, d);
        c.Items(d, "switches", c.Count(d, "switches", 'I'), (c2, x) =>
        {
            c2.Val(x, "switch", 'I');
            Ids(c2, x, "nodes");
        });
        c.Items(d, "params", c.Count(d, "params", 'I'), (c2, x) => Vals(c2, x, ("node", 'I'), ("first_only", 'B'),
            ("continue_playback", 'B'), ("on_switch", 'I'), ("fade_out", 'i'), ("fade_in", 'i')));
    }
    private static void ActorMixer(HircCodec c, JObj d)
    {
        NodeBase(c, d);
        Children(c, d);
    }
    private static void Blend(HircCodec c, JObj d)
    {
        NodeBase(c, d);
        Children(c, d);
        c.Items(d, "layers", c.Count(d, "layers", 'I'), (c2, x) =>
        {
            c2.Val(x, "layer", 'I');
            Rtpcs(c2, x);
            c2.Val(x, "rtpc", 'I');
            c2.Items(x, "assoc", c2.Count(x, "assoc", 'I'), (c3, y) =>
            {
                c3.Val(y, "child", 'I');
                Graph(c3, y, "points", 'I');
            });
        });
    }
    private static void Event(HircCodec c, JObj d)
    {
        Ids(c, d, "actions");
    }
    private static void Action(HircCodec c, JObj d)
    {
        long t = c.Enum(d, "action", 'H', s_actions);
        c.Val(d, "target", 'I');
        Bundle(c, d, "props");
        Bundle(c, d, "ranged_props", true);
        long kind = t >> 8;
        if (kind is 0x04 or 0x05)
        {
            c.Val(d, "fade_curve", 'B');
            c.Val(d, "bank", 'I');
        }
        else if (kind is 0x01 or 0x02 or 0x03 or 0x06 or 0x07 or 0x1B or 0x1C)
        {
            c.Val(d, "fade_curve", 'B');
            if (kind is 0x02 or 0x03)
            {
                c.Val(d, "master", 'B');
            }
            Ids(c, d, "exceptions");
        }
        else if (kind is (>= 0x08 and <= 0x0F) or 0x13 or 0x14)
        {
            Vals(c, d, ("fade_curve", 'B'), ("meaning", 'B'), ("base", 'f'), ("min", 'f'), ("max", 'f'));
            Ids(c, d, "exceptions");
        }
        else if (kind == 0x12)
        {
            Vals(c, d, ("group", 'I'), ("state", 'I'));
        }
        else if (kind == 0x19)
        {
            Vals(c, d, ("group", 'I'), ("switch", 'I'));
        }
        else if (kind == 0x1A)
        {
            Vals(c, d, ("bypass", 'B'), ("mask", 'B'));
            Ids(c, d, "exceptions");
        }
        else if (kind == 0x1E)
        {
            Vals(c, d, ("relative", 'B'), ("value", 'f'), ("min", 'f'), ("max", 'f'), ("snap", 'B'));
            Ids(c, d, "exceptions");
        }
    }
    private static void Bus(HircCodec c, JObj d)
    {
        c.Val(d, "override_bus", 'I');
        Bundle(c, d, "props");
        Vals(c, d, ("kill_newest", 'B'), ("use_virtual", 'B'), ("max_instances", 'H'), ("max_instances_override", 'B'),
            ("channels", 'H'), ("unused1", 'B'), ("unused2", 'B'), ("environmental", 'B'),
            ("recovery_time", 'i'), ("max_duck_volume", 'f'));
        c.Items(d, "ducks", c.Count(d, "ducks", 'I'), (c2, x) => Vals(c2, x, ("bus", 'I'), ("volume", 'f'), ("fade_out", 'i'),
            ("fade_in", 'i'), ("curve", 'B')));
        Fx(c, d);
        Rtpcs(c, d);
        States(c, d);
    }
    private static void Attenuation(HircCodec c, JObj d)
    {
        if (c.Val(d, "cone", 'B') != 0)
        {
            Vals(c, d, ("inside", 'f'), ("outside", 'f'), ("outside_volume", 'f'), ("low_pass", 'f'));
        }
        for (int k = 0; k < 5; k++)
        {
            c.Val(d, $"curve{k}", 'B');
        }
        c.Items(d, "curves", c.Count(d, "curves", 'B'), (c2, x) =>
        {
            c2.Enum(x, "scaling", 'B', s_scaling);
            Graph(c2, x, "points", 'H');
        });
        Rtpcs(c, d);
    }
    private static void MusicNode(HircCodec c, JObj d)
    {
        NodeBase(c, d);
        Children(c, d);
        Vals(c, d, ("grid_period", 'd'), ("grid_offset", 'd'), ("tempo", 'f'), ("beats", 'B'), ("beat_value", 'B'), ("meter_override", 'B'));
        c.Items(d, "stingers", c.Count(d, "stingers", 'I'), (c2, x) => Vals(c2, x, ("trigger", 'I'), ("segment", 'I'),
            ("sync", 'I'), ("no_repeat", 'i'), ("look_ahead", 'I')));
    }
    private static void MusicSegment(HircCodec c, JObj d)
    {
        MusicNode(c, d);
        c.Val(d, "duration", 'd');
        c.Items(d, "markers", c.Count(d, "markers", 'I'), (c2, x) => Vals(c2, x, ("id", 'I'), ("position", 'd')));
    }
    private static void MusicTrack(HircCodec c, JObj d)
    {
        c.Items(d, "sources", c.Count(d, "sources", 'I'), Source);
        long n = c.Count(d, "playlist", 'I');
        c.Items(d, "playlist", n, (c2, x) => Vals(c2, x, ("track", 'I'), ("source", 'I'), ("play_at", 'd'),
            ("begin_trim", 'd'), ("end_trim", 'd'), ("duration", 'd')));
        if (n != 0)
        {
            c.Val(d, "subtracks", 'I');
        }
        NodeBase(c, d);
        c.Val(d, "rs_type", 'I');
        c.Val(d, "look_ahead", 'i');
    }
    private static void Transitions(HircCodec c, JObj d)
    {
        MusicNode(c, d);
        c.Items(d, "rules", c.Count(d, "rules", 'I'), (c2, x) => Vals(c2, x,
            ("source", 'I'), ("destination", 'I'), ("src_time", 'i'), ("src_curve", 'I'), ("src_offset", 'i'), ("src_sync", 'I'),
            ("src_post_exit", 'B'), ("dst_time", 'i'), ("dst_curve", 'I'), ("dst_offset", 'i'),
            ("dst_marker", 'I'), ("dst_jump_to", 'I'), ("dst_entry", 'H'), ("dst_pre_entry", 'B'),
            ("transition_object", 'B'), ("segment", 'I'), ("fade_in_time", 'i'), ("fade_in_curve", 'I'), ("fade_in_offset", 'i'),
            ("fade_out_time", 'i'), ("fade_out_curve", 'I'), ("fade_out_offset", 'i'),
            ("pre_entry", 'B'), ("post_exit", 'B')));
    }
    private static void MusicSwitch(HircCodec c, JObj d)
    {
        Transitions(c, d);
        Vals(c, d, ("group_type", 'I'), ("group", 'I'), ("default", 'I'), ("continuous", 'B'));
        c.Items(d, "switches", c.Count(d, "switches", 'I'), (c2, x) => Vals(c2, x, ("switch", 'I'), ("node", 'I')));
    }
    private static void PlaylistItem(HircCodec c, JObj x)
    {
        c.Val(x, "segment", 'I');
        c.Val(x, "id", 'I');
        long n = c.Count(x, "children", 'I');
        Vals(c, x, ("rs_type", 'I'), ("loop", 'h'), ("weight", 'I'), ("avoid_repeat", 'H'), ("use_weight", 'B'), ("shuffle", 'B'));
        c.Items(x, "children", n, PlaylistItem);
    }
    private static void MusicPlaylist(HircCodec c, JObj d)
    {
        Transitions(c, d);
        c.Val(d, "playlist_count", 'I');
        c.Items(d, "root", 1, PlaylistItem);
    }
    private static void FxObject(HircCodec c, JObj d)
    {
        c.Val(d, "plugin", 'I');
        if (c is HircReader r)
        {
            long size = r.GetLong('I');
            byte[] raw = Bin.Slice(r.B, r.O, (int)Math.Min(int.MaxValue, r.O + size)).ToArray();
            r.O += (int)size;
            if (size % 4 == 0)
            {
                HircReader vals = new(raw, r.Big);
                List<object?> list = [];
                for (long i = 0; i < size / 4; i++)
                {
                    list.Add(vals.Prop([], "v"));
                }
                d["params"] = list;
            }
            else
            {
                d["params"] = Bin.Hex(raw);
            }
        }
        else
        {
            HircWriter w = (HircWriter)c;
            object? p = d["params"];
            ByteWriter raw = new();
            if (p is string hex)
            {
                raw.Add(Bin.FromHex(hex));
            }
            else
            {
                foreach (object? v in J.A(p))
                {
                    if (v is double dv)
                    {
                        raw.F32(dv, w.Big);
                    }
                    else
                    {
                        raw.U32(J.U(v), w.Big);
                    }
                }
            }
            w.Out.U32((uint)raw.Length, w.Big);
            w.Out.Add(raw.Span);
        }
        if (c.Val(d, "media", 'B') != 0 || c.Val(d, "rtpc_count", 'H') != 0)
        {
            throw new StructException("effect media or RTPCs are not described");
        }
    }
    private static void Settings(HircCodec c, JObj d)
    {
        Bundle(c, d, "props");
    }
    private static readonly Dictionary<long, Action<HircCodec, JObj>> s_types = new()
    {
        [1] = Settings, [2] = Sound, [3] = Action, [4] = Event, [5] = RandomSequence, [6] = Switch, [7] = ActorMixer, [9] = Blend,
        [8] = Bus, [10] = MusicSegment, [11] = MusicTrack, [12] = MusicSwitch, [13] = MusicPlaylist, [14] = Attenuation,
        [18] = FxObject, [19] = FxObject,
    };
    private static void Bkhd(HircCodec c, JObj d)
    {
        Vals(c, d, ("version", 'I'), ("bank", 'I'), ("language", 'I'), ("feedback", 'I'));
        if (c is HircReader r)
        {
            ReadOnlySpan<byte> rest = r.B.AsSpan(Math.Min(r.O, r.B.Length));
            if (Bin.AnyNonZero(rest))
            {
                throw new StructException("header padding is not zero");
            }
            d["padding"] = (long)rest.Length;
            r.O = r.B.Length;
        }
        else
        {
            ((HircWriter)c).Out.Zeros((int)J.L(d["padding"]));
        }
    }
    private static void Stid(HircCodec c, JObj d)
    {
        c.Val(d, "type", 'I');
        c.Items(d, "names", c.Count(d, "names", 'I'), (c2, x) =>
        {
            c2.Val(x, "bank", 'I');
            if (c2 is HircReader r)
            {
                int n = (int)r.GetLong('B');
                x["name"] = Bin.Latin1(Bin.Slice(r.B, r.O, r.O + n));
                r.O += n;
            }
            else
            {
                byte[] raw = Bin.Latin1(J.S(x["name"]));
                if (raw.Length > 255)
                {
                    throw new InvalidDataException("bytes must be in range(0, 256)");
                }
                ((HircWriter)c2).Out.Add((byte)raw.Length);
                ((HircWriter)c2).Out.Add(raw);
            }
        });
    }
    private static void Stmg(HircCodec c, JObj d)
    {
        c.Val(d, "volume_threshold", 'f');
        c.Val(d, "max_voices", 'H');
        c.Items(d, "state_groups", c.Count(d, "state_groups", 'I'), (c2, x) =>
        {
            c2.Val(x, "group", 'I');
            c2.Val(x, "default_time", 'I');
            c2.Items(x, "transitions", c2.Count(x, "transitions", 'I'), (c3, y) => Vals(c3, y, ("from", 'I'), ("to", 'I'), ("time", 'I')));
        });
        c.Items(d, "switch_groups", c.Count(d, "switch_groups", 'I'), (c2, x) =>
        {
            c2.Val(x, "group", 'I');
            c2.Val(x, "rtpc", 'I');
            Graph(c2, x, "points", 'I');
        });
        c.Items(d, "params", c.Count(d, "params", 'I'), (c2, x) => Vals(c2, x, ("rtpc", 'I'), ("value", 'f')));
    }
    private static void Envs(HircCodec c, JObj d)
    {
        c.Items(d, "curves", 4, (c2, x) =>
        {
            c2.Val(x, "enabled", 'B');
            c2.Val(x, "scaling", 'B');
            Graph(c2, x, "points", 'H');
        });
    }
    private static readonly Dictionary<string, Action<HircCodec, JObj>> s_chunks = new()
    {
        ["BKHD"] = Bkhd,
        ["STID"] = Stid,
        ["STMG"] = Stmg,
        ["ENVS"] = Envs,
    };
    public static bool IsDescribedChunk(string cid)
    {
        return s_chunks.ContainsKey(cid);
    }
    public static JObj? DecodeChunk(string cid, byte[] body, bool big)
    {
        JObj d = [];
        HircReader r = new(body, big);
        try
        {
            s_chunks[cid](r, d);
        }
        catch (Exception ex) when (ex is StructException or KeyNotFoundException)
        {
            return null;
        }
        return r.Done && EncodeChunk(cid, d, big).AsSpan().SequenceEqual(body) ? d : null;
    }
    public static byte[] EncodeChunk(string cid, JObj d, bool big)
    {
        HircWriter w = new(big);
        s_chunks[cid](w, d);
        return w.Out.ToArray();
    }
    public static JObj? Decode(long t, byte[] body, bool big)
    {
        if (!s_types.TryGetValue(t, out Action<HircCodec, JObj>? fn))
        {
            return null;
        }
        JObj d = [];
        HircReader r = new(body, big);
        try
        {
            fn(r, d);
        }
        catch (StructException)
        {
            return null;
        }
        return r.Done && Encode(t, d, big).AsSpan().SequenceEqual(body) ? d : null;
    }
    public static byte[] Encode(long t, JObj d, bool big)
    {
        HircWriter w = new(big);
        s_types[t](w, d);
        return w.Out.ToArray();
    }
}
