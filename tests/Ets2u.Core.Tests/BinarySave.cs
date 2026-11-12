using System.Text;

namespace Ets2u.Core.Tests;

/// <summary>Builds a tiny binary (BSII v3) save in the game's layout, for tests.</summary>
public static class BinarySave
{
    public static byte[] Build(long money, uint truckXp, uint coachXp, int? extraFieldType = null)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        w.Write("BSII"u8);
        w.Write(3u);

        void Str(string v) { var b = Encoding.UTF8.GetBytes(v); w.Write((uint)b.Length); w.Write(b); }
        void Nameless(ulong id) { w.Write((byte)0xFF); w.Write(id); }

        // Structure 1: bank { money_account: s64, company: string }
        w.Write(0u); w.Write((byte)1); w.Write(1u); Str("bank");
        w.Write(0x31u); Str("money_account");
        w.Write(0x01u); Str("company");
        if (extraFieldType is { } t) { w.Write((uint)t); Str("mystery"); }
        w.Write(0u);

        // Structure 2: economy { experience_points: u32, trucks: id array (v3), bus_experience_points: u32, hq: placement }
        w.Write(0u); w.Write((byte)1); w.Write(2u); Str("economy");
        w.Write(0x27u); Str("experience_points");
        w.Write(0x3Cu); Str("trucks");
        w.Write(0x27u); Str("bus_experience_points");
        w.Write(0x19u); Str("hq");
        w.Write(0u);

        // bank record
        w.Write(1u); Nameless(0x1234);
        w.Write(money);
        Str("Kowloon Motor Trucks");
        if (extraFieldType is not null) w.Write(0u);

        // economy record
        w.Write(2u); Nameless(0x5678);
        w.Write(truckXp);
        w.Write(2u); Nameless(0xAAAA); w.Write((byte)2); w.Write(11ul); w.Write(22ul);
        w.Write(coachXp);
        for (var i = 0; i < 8; i++) w.Write(1.5f);

        // end marker
        w.Write(0u); w.Write((byte)0);
        return s.ToArray();
    }
}
