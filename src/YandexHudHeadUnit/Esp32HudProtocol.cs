using System.Buffers.Binary;
using System.Text;
using SourceState = YandexNotificationBridge.BridgeState;

namespace YandexHudHeadUnit;

// Encoder for the frozen ESP32 HUD BLE protocol v1. This file is deliberately
// Android-free so the console test project can exercise it without a device.
internal static class Esp32HudProtocol
{
    internal const int ManeuverFreshMs = 5000;
    internal const int CameraFreshMs = 10000;
    internal const int ManeuverHoldMs = 4000;
    internal const int MaxTextBytes = 64;

    internal static byte ModeOf(SourceState state) => state.Mode switch
    {
        "route" when state.Active => Esp32HudProtocolConstants.MODE_ROUTE,
        "route" => Esp32HudProtocolConstants.MODE_IDLE,
        "free" => Esp32HudProtocolConstants.MODE_FREE,
        "idle" => Esp32HudProtocolConstants.MODE_IDLE,
        _ => state.Active ? Esp32HudProtocolConstants.MODE_ROUTE : Esp32HudProtocolConstants.MODE_IDLE
    };

    internal static byte KindOf(string? maneuver) => maneuver switch
    {
        "Left" => Esp32HudProtocolConstants.MANEUVER_KIND_LEFT,
        "Right" => Esp32HudProtocolConstants.MANEUVER_KIND_RIGHT,
        "LeftWide" or "LeftFork" => Esp32HudProtocolConstants.MANEUVER_KIND_LEFT_WIDE,
        "RightWide" or "RightFork" => Esp32HudProtocolConstants.MANEUVER_KIND_RIGHT_WIDE,
        "LeftSharp" => Esp32HudProtocolConstants.MANEUVER_KIND_LEFT_SHARP,
        "RightSharp" => Esp32HudProtocolConstants.MANEUVER_KIND_RIGHT_SHARP,
        "UTurnLeft" => Esp32HudProtocolConstants.MANEUVER_KIND_U_TURN_LEFT,
        "UTurnRight" => Esp32HudProtocolConstants.MANEUVER_KIND_U_TURN_RIGHT,
        "Straight" => Esp32HudProtocolConstants.MANEUVER_KIND_STRAIGHT,
        "Finish" => Esp32HudProtocolConstants.MANEUVER_KIND_FINISH,
        "Rerouting" => Esp32HudProtocolConstants.MANEUVER_KIND_REROUTING,
        "Roundabout" => Esp32HudProtocolConstants.MANEUVER_KIND_ROUNDABOUT,
        _ => 0
    };

    internal static bool IsFresh(long stamp, long now, int maxAgeMs) =>
        stamp > 0 && stamp <= now + 5000 && now - stamp <= maxAgeMs;

    internal static byte[] EncodeState(SourceState state, bool lanesEnabled, long now, uint sequence,
        bool includeMeta = true)
    {
        var tlvs = new List<byte>(160);
        byte mode = ModeOf(state);
        bool route = mode == Esp32HudProtocolConstants.MODE_ROUTE;
        bool free = mode == Esp32HudProtocolConstants.MODE_FREE;
        bool driving = route || free;
        bool fresh = IsFresh(state.ObservedAtUnixMilliseconds, now, ManeuverFreshMs);
        bool rerouting = state.Maneuver == "Rerouting";

        AddTlv(tlvs, Esp32HudProtocolConstants.GroupG0_SESSION, [mode]);

        if (route && fresh)
        {
            byte kind = KindOf(state.Maneuver);
            if (kind != 0)
            {
                uint distance = kind == Esp32HudProtocolConstants.MANEUVER_KIND_REROUTING
                    ? 0
                    : (uint)Clamp(state.NextTurnDistanceMeters, 0, 2_000_000);
                var value = new byte[5];
                value[0] = kind;
                BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(1), distance);
                AddTlv(tlvs, Esp32HudProtocolConstants.GroupG1_MANEUVER, value);
            }

            if (lanesEnabled && !rerouting && ParseLanes(state.Lanes, state.LanesObservedAtUnixMilliseconds, now) is { } lanes)
            {
                var value = new byte[1 + lanes.Count];
                value[0] = (byte)lanes.Count;
                lanes.CopyTo(value, 1);
                AddTlv(tlvs, Esp32HudProtocolConstants.GroupG2_LANES, value);
            }

            AddText(tlvs, Esp32HudProtocolConstants.GroupG3_NEXT_STREET, state.Road);
        }

        if (driving)
        {
            AddText(tlvs, Esp32HudProtocolConstants.GroupG4_CURRENT_STREET, state.CurrentRoad);

            if (state.SpeedLimitKph is >= 5 and <= 200)
                AddTlv(tlvs, Esp32HudProtocolConstants.GroupG5_SPEED_LIMIT, [(byte)state.SpeedLimitKph]);
        }

        if (driving && !rerouting &&
            IsFresh(state.CameraObservedAtUnixMilliseconds, now, CameraFreshMs) &&
            state.CameraDistanceMeters is >= 1 and <= 5000)
        {
            AddU16(tlvs, Esp32HudProtocolConstants.GroupG6_CAMERA, (ushort)state.CameraDistanceMeters);
        }

        if (driving && RoadEventKind(state.RoadEvent) is { } eventKind)
        {
            var value = new byte[3];
            value[0] = eventKind;
            BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(1), (ushort)Clamp(state.RoadEventDistanceMeters, 0, 5000));
            AddTlv(tlvs, Esp32HudProtocolConstants.GroupG7_ROAD_EVENT, value);
        }

        if (route && fresh && Remaining(state.RemainingDistanceMeters, state.RemainingTimeSeconds) is { } remaining)
        {
            var value = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(value, remaining.Distance);
            BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), remaining.Time);
            AddTlv(tlvs, Esp32HudProtocolConstants.GroupG8_REMAINING, value);
        }

        if (includeMeta && state.ObservedAtUnixMilliseconds > 0)
        {
            var value = new byte[10];
            value[0] = state.Source == "screen"
                ? Esp32HudProtocolConstants.G10_SOURCE_SCREEN
                : Esp32HudProtocolConstants.G10_SOURCE_NOTIFICATION;
            value[1] = (byte)(state.ScreenReaderConnected ? 1 : 0);
            BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(2), state.ObservedAtUnixMilliseconds);
            AddTlv(tlvs, Esp32HudProtocolConstants.GroupG10_META, value);
        }

        return BuildMessage(Esp32HudProtocolConstants.TypeStateSnapshot, sequence, tlvs);
    }

    internal static byte[] EncodePosition(HudLocation location, uint sequence)
    {
        var value = new byte[36];
        BinaryPrimitives.WriteDoubleLittleEndian(value, location.Latitude);
        BinaryPrimitives.WriteDoubleLittleEndian(value.AsSpan(8), location.Longitude);
        WriteFloat(value.AsSpan(16), location.SpeedMetersPerSecond);
        WriteFloat(value.AsSpan(20), location.BearingDegrees);
        WriteFloat(value.AsSpan(24), location.AccuracyMeters);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(28), location.TimestampUnixMilliseconds);
        var tlvs = new List<byte>(40);
        AddTlv(tlvs, Esp32HudProtocolConstants.GroupG9_POSITION, value);
        return BuildMessage(Esp32HudProtocolConstants.TypePosition, sequence, tlvs);
    }

    internal static byte[] EncodeIdle(uint sequence)
    {
        var tlvs = new List<byte>(4);
        AddTlv(tlvs, Esp32HudProtocolConstants.GroupG0_SESSION, [Esp32HudProtocolConstants.MODE_IDLE]);
        return BuildMessage(Esp32HudProtocolConstants.TypeStateSnapshot, sequence, tlvs);
    }

    internal static IReadOnlyList<byte[]> Fragment(byte[] message, int mtu)
    {
        if (mtu < 7) throw new ArgumentOutOfRangeException(nameof(mtu));
        if (message.Length <= mtu - 3) return [message];
        int dataPerChunk = mtu - 6;
        int total = (message.Length + dataPerChunk - 1) / dataPerChunk;
        if (total > 255) throw new InvalidOperationException("fragment count exceeds 255");
        var chunks = new byte[total][];
        for (int i = 0; i < total; i++)
        {
            int offset = i * dataPerChunk;
            int count = Math.Min(dataPerChunk, message.Length - offset);
            var chunk = new byte[3 + count];
            chunk[0] = Esp32HudProtocolConstants.FragmentMagic;
            chunk[1] = (byte)i;
            chunk[2] = (byte)total;
            message.AsSpan(offset, count).CopyTo(chunk.AsSpan(3));
            chunks[i] = chunk;
        }
        return chunks;
    }

    internal static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    private static byte[] BuildMessage(byte type, uint sequence, List<byte> tlvs)
    {
        var message = new byte[8 + tlvs.Count + 2];
        message[0] = Esp32HudProtocolConstants.Version;
        message[1] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(2), sequence);
        // bytes 6..7 are reserved zero
        tlvs.CopyTo(message, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(message.Length - 2),
            Crc16(message.AsSpan(0, message.Length - 2)));
        return message;
    }

    private static void AddTlv(List<byte> tlvs, byte group, ReadOnlySpan<byte> value)
    {
        tlvs.Add(group);
        tlvs.Add((byte)value.Length);
        foreach (byte b in value) tlvs.Add(b);
    }

    private static void AddU16(List<byte> tlvs, byte group, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        AddTlv(tlvs, group, bytes);
    }

    private static void AddText(List<byte> tlvs, byte group, string? text)
    {
        var bytes = Utf8(text);
        if (bytes.Length > 0) AddTlv(tlvs, group, bytes);
    }

    private static byte[] Utf8(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is < 0x20 or 0x7F) continue;   // control characters/newlines
            if (rune.Value > 0xFFFF) continue;            // surrogate pairs / emoji
            builder.Append(rune.ToString());
        }
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        if (bytes.Length <= MaxTextBytes) return bytes;
        int length = MaxTextBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
        return bytes[..length];
    }

    private static List<byte>? ParseLanes(string? lanes, long stamp, long now)
    {
        if (!IsFresh(stamp, now, ManeuverFreshMs) || string.IsNullOrEmpty(lanes)) return null;
        var items = lanes.Split(',');
        if (items.Length is < 1 or > 8) return null;
        var result = new List<byte>(items.Length);
        foreach (var item in items)
        {
            bool recommended = item.EndsWith("!", StringComparison.Ordinal);
            var name = recommended ? item[..^1] : item;
            byte direction = name switch
            {
                "S" => Esp32HudProtocolConstants.LANE_DIRECTION_STRAIGHT,
                "L" => Esp32HudProtocolConstants.LANE_DIRECTION_LEFT,
                "R" => Esp32HudProtocolConstants.LANE_DIRECTION_RIGHT,
                "SR" => Esp32HudProtocolConstants.LANE_DIRECTION_SLIGHT_RIGHT,
                "U" => Esp32HudProtocolConstants.LANE_DIRECTION_U_TURN_LEFT,
                _ => 0
            };
            if (direction == 0) return null;
            result.Add((byte)(direction | (recommended ? 0x80 : 0)));
        }
        return result;
    }

    private static byte? RoadEventKind(string? roadEvent) => roadEvent switch
    {
        "ДТП" or "accident" => Esp32HudProtocolConstants.ROAD_EVENT_KIND_ACCIDENT,
        "Дорожные работы" or "roadworks" => Esp32HudProtocolConstants.ROAD_EVENT_KIND_ROADWORKS,
        "Перекрытие" or "closed" => Esp32HudProtocolConstants.ROAD_EVENT_KIND_CLOSED,
        "Опасность" or "hazard" => Esp32HudProtocolConstants.ROAD_EVENT_KIND_HAZARD,
        "Лежачий полицейский" or "speed_bump" => Esp32HudProtocolConstants.ROAD_EVENT_KIND_SPEED_BUMP,
        _ => null
    };

    private static (uint Distance, uint Time)? Remaining(double distance, double seconds)
    {
        if (!double.IsFinite(distance) || !double.IsFinite(seconds) ||
            distance < 0 || distance > 50_000_000 || seconds < 0 || seconds > 3_000_000)
            return null;
        uint d = (uint)Math.Round(distance, MidpointRounding.AwayFromZero);
        uint t = (uint)Math.Round(seconds, MidpointRounding.AwayFromZero);
        return d == 0 && t == 0 ? null : (d, t);
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : 0;

    private static void WriteFloat(Span<byte> destination, double? value)
    {
        if (value is { } number && double.IsFinite(number))
            BinaryPrimitives.WriteSingleLittleEndian(destination, (float)number);
        else
            BinaryPrimitives.WriteInt32LittleEndian(destination, unchecked((int)0x7FC00000));
    }
}

// Stateful sender: per-type sequence counters and the 4 s G1 hold policy.
internal sealed class Esp32HudEncoder
{
    private uint _stateSequence;
    private uint _positionSequence;
    private HeldManeuver? _hold;

    private readonly record struct HeldManeuver(string Maneuver, uint Distance, string? Street, long AtMs);

    internal byte[] BuildState(SourceState state, bool lanesEnabled, long now, bool includeMeta = true)
    {
        var (maneuver, distance, road) = ApplyHold(state, now);
        var effective = state with { Maneuver = maneuver, NextTurnDistanceMeters = distance, Road = road };
        return Esp32HudProtocol.EncodeState(effective, lanesEnabled, now, ++_stateSequence, includeMeta);
    }

    internal byte[] BuildPosition(HudLocation location) =>
        Esp32HudProtocol.EncodePosition(location, ++_positionSequence);

    internal byte[] BuildIdle()
    {
        _hold = null;
        return Esp32HudProtocol.EncodeIdle(++_stateSequence);
    }

    private (string? Maneuver, double Distance, string? Road) ApplyHold(SourceState state, long now)
    {
        if (Esp32HudProtocol.ModeOf(state) != Esp32HudProtocolConstants.MODE_ROUTE)
        {
            _hold = null;
            return (state.Maneuver, state.NextTurnDistanceMeters, state.Road);
        }

        byte kind = Esp32HudProtocol.KindOf(state.Maneuver);
        bool fresh = Esp32HudProtocol.IsFresh(state.ObservedAtUnixMilliseconds, now, Esp32HudProtocol.ManeuverFreshMs);
        if (kind != 0 && fresh)
        {
            uint distance = kind == Esp32HudProtocolConstants.MANEUVER_KIND_REROUTING
                ? 0
                : (uint)Math.Clamp(state.NextTurnDistanceMeters, 0, 2_000_000);
            _hold = new HeldManeuver(state.Maneuver!, distance, state.Road, now);
            return (state.Maneuver, state.NextTurnDistanceMeters, state.Road);
        }

        if (_hold is { } held && now - held.AtMs <= Esp32HudProtocol.ManeuverHoldMs &&
            held.Distance > 0 &&
            (string.IsNullOrEmpty(state.Road) || string.Equals(held.Street, state.Road, StringComparison.Ordinal)))
            return (held.Maneuver, held.Distance, held.Street);

        _hold = null;
        return (state.Maneuver, state.NextTurnDistanceMeters, state.Road);
    }
}
