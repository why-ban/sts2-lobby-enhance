using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Daily;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Sts2LobbyEnhance;

/// <summary>
/// Repair for the desktop-host join response body.
///
/// LAN Connect ships two different serialisation paths for <see cref="ClientLobbyJoinResponseMessage"/>:
///
/// <list type="bullet">
/// <item>the per-message <c>T.Serialize</c> transpiler, which only rewrites the list bit width and is
/// therefore layout-agnostic — it survives the 0.113 field additions untouched;</item>
/// <item>a hand-written body (<c>LanConnectSerializationPatches.SerializeJoinResponseBody</c>) that the
/// message-bus boundary prefix calls on desktop so the host mod owns the entire serialized frame for
/// this one message (the generic <c>NetMessageBus.SerializeMessage&lt;T&gt;</c> cannot be transpiled on
/// every runtime — Android's gshared closed-generic wrappers defeat Harmony). That body spells the field
/// order out by hand and was written against 0.111, so it omits the two fields 0.113 added to this
/// message: <c>gameMode</c> (3 bits) and <c>isCustom</c> (1 bit).</item>
/// </list>
///
/// Four missing bits shift everything from <c>gameMode</c> onwards: the receiver reads the low four bits
/// of <c>ascension</c> as <c>gameMode</c> + <c>isCustom</c>, then reads <c>ascension</c> out of the low
/// four bits of the <c>modifiers</c> list header, then reads the <c>modifiers</c> count four bits past the
/// end of the body. Whether that ends in <c>IndexOutOfRangeException</c> or in a silently corrupted lobby
/// depends on the count that comes out of those bits; both were observed.
///
/// The hand-written body is plain IL with no references to removed members, so unlike the join flow it
/// can be patched normally. We skip the original and emit the 0.113 layout ourselves. The list bit width
/// is still taken from the host mod's own profile logic so compat_4_5_v1 and tail_v1 keep behaving
/// differently exactly as before.
/// </summary>
internal sealed class HostWireFix : ICompatFix
{
    private const string SerializationPatchesTypeName = "Sts2LanConnect.Scripts.LanConnectSerializationPatches";
    private const string BodyMethodName = "SerializeJoinResponseBody";
    private const int AscensionBits = 5;

    // Probe targets. Every one of these is a *string* on purpose: a probe method that contains
    // typeof(<game type>) cannot be JITted once that type is gone, which is exactly the failure the probe
    // exists to survive (see Core/Reflect.cs and the 2026-11-14 gating audit).
    private const string GameAssemblyName = "sts2";
    private const string GameAssemblyAnchorTypeName = "MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter";
    private const string PacketWriterTypeName = "MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter";
    private const string JoinResponseTypeName = "MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.ClientLobbyJoinResponseMessage";
    private const string GameModeTypeName = "MegaCrit.Sts2.Core.Runs.GameMode";

    /// <summary>The <see cref="PacketWriter"/> members <see cref="SerializeJoinResponseBodyPrefix"/> calls.</summary>
    private static readonly string[] WriterMembers =
    {
        "WriteList", "WriteBool", "Write", "WriteString", "WriteEnum", "WriteInt"
    };

    /// <summary>Body-only bit count the message-bus boundary adds its 8-bit id and 64-bit sender to.</summary>
    private const int MessageBusHeaderBits = 8 + 64;

    /// <summary>Set once the first real body has been emitted, so the diagnostic costs one line per session.</summary>
    private static int _reported;

    public string Id => "host-wire";

    public FixGroup Group => FixGroup.Wire;

    /// <summary>
    /// No snapshot flag on purpose. The precise test is the message's own field set, which <see cref="TryCapture"/>
    /// checks: <c>gameMode</c> + <c>isCustom</c> present on <c>ClientLobbyJoinResponseMessage</c> and the seven
    /// fields the body writes. Those are exactly what the shipped 0.111 hand-written body omits, so "fields
    /// present" is the condition under which this repair is both needed and meaningful — and if a future build
    /// reshapes the message, the field checks fail and the repair stands down.
    /// </summary>
    public bool Applies(in CompatSnapshot snapshot, out string reason)
    {
        _ = snapshot;

        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod '" + Entry.HostModAssemblyName + "' is not loaded";
            return false;
        }

        if (!TryCapture(host, out Shape shape, out string missing))
        {
            reason = "missing: " + missing;
            return false;
        }

        _shape = shape;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Everything <see cref="Apply"/> and the prefix body need, resolved *before* a single patch is installed.
    ///
    /// The seven message fields are stored for detection only — the body reads them through the compiler — so a
    /// renamed or removed field skips the repair at startup instead of failing the JIT of the prefix on the one
    /// frame that carries a join response.
    /// </summary>
    internal sealed class Shape
    {
        internal MethodInfo Body = null!;

        internal FieldInfo? PlayersInLobby;

        internal FieldInfo? DailyTime;

        internal FieldInfo? Seed;

        internal FieldInfo? GameMode;

        internal FieldInfo? IsCustom;

        internal FieldInfo? Ascension;

        internal FieldInfo? Modifiers;
    }

    internal static Shape? _shape;

    /// <summary>
    /// Resolves every anchor this repair needs. Never throws, never invokes anything, never touches Godot:
    /// pure metadata over the already-loaded host assembly and the game assembly. The returned reason lists
    /// *all* missing items, so one log line says everything that has to be fixed.
    /// </summary>
    private static bool TryCapture(Assembly host, out Shape shape, out string reason)
    {
        shape = new Shape();
        var missing = new List<string>();

        Type? patchesType = host.GetType(SerializationPatchesTypeName, throwOnError: false, ignoreCase: false);
        if (patchesType == null)
        {
            missing.Add("type " + SerializationPatchesTypeName);
        }
        else
        {
            MethodInfo? body = patchesType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, BodyMethodName, StringComparison.Ordinal)
                    && method.GetParameters() is { Length: 3 } parameters
                    && string.Equals(parameters[0].ParameterType.FullName, PacketWriterTypeName, StringComparison.Ordinal)
                    && string.Equals(parameters[1].ParameterType.FullName, JoinResponseTypeName, StringComparison.Ordinal)
                    && parameters[2].ParameterType == typeof(int));

            if (body == null)
            {
                missing.Add($"{SerializationPatchesTypeName}.{BodyMethodName}({PacketWriterTypeName}, {JoinResponseTypeName}, int)");
            }
            else
            {
                shape.Body = body;
            }
        }

        Assembly? game = ResolveGameAssembly();
        if (game == null)
        {
            missing.Add("game assembly '" + GameAssemblyName + "'");
        }
        else
        {
            Type? response = game.GetType(JoinResponseTypeName, throwOnError: false, ignoreCase: false);
            if (response == null)
            {
                missing.Add("type " + JoinResponseTypeName);
            }
            else
            {
                shape.PlayersInLobby = ReflectScope.InstanceFieldOf(response, "playersInLobby", missing);
                shape.DailyTime = ReflectScope.InstanceFieldOf(response, "dailyTime", missing);
                shape.Seed = ReflectScope.InstanceFieldOf(response, "seed", missing);
                shape.GameMode = ReflectScope.InstanceFieldOf(response, "gameMode", missing);
                shape.IsCustom = ReflectScope.InstanceFieldOf(response, "isCustom", missing);
                shape.Ascension = ReflectScope.InstanceFieldOf(response, "ascension", missing);
                shape.Modifiers = ReflectScope.InstanceFieldOf(response, "modifiers", missing);

                if (shape.GameMode != null
                    && !string.Equals(shape.GameMode.FieldType.FullName, GameModeTypeName, StringComparison.Ordinal))
                {
                    missing.Add($"field gameMode is {shape.GameMode.FieldType.FullName}, expected {GameModeTypeName}");
                }

                if (shape.IsCustom != null && shape.IsCustom.FieldType != typeof(bool))
                {
                    missing.Add($"field isCustom is {shape.IsCustom.FieldType.Name}, expected Boolean");
                }
            }

            Type? writer = game.GetType(PacketWriterTypeName, throwOnError: false, ignoreCase: false);
            if (writer == null)
            {
                missing.Add("type " + PacketWriterTypeName);
            }
            else
            {
                foreach (string member in WriterMembers)
                {
                    if (writer
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .All(method => !string.Equals(method.Name, member, StringComparison.Ordinal)))
                    {
                        missing.Add($"{PacketWriterTypeName}.{member}");
                    }
                }

                if (writer.GetProperty(
                        "BitPosition",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null)
                {
                    missing.Add($"{PacketWriterTypeName}.BitPosition");
                }
            }
        }

        reason = string.Join(", ", missing);
        return missing.Count == 0;
    }

    private static Assembly? ResolveGameAssembly()
    {
        Assembly? direct = Reflect.FindAssembly(GameAssemblyName);
        if (direct != null)
        {
            return direct;
        }

        // The simple name is stable but not contractual; fall back to whichever loaded assembly owns a core
        // serialization type rather than assuming.
        foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (candidate.GetType(GameAssemblyAnchorTypeName, throwOnError: false) != null)
                {
                    return candidate;
                }
            }
            catch
            {
                // Some dynamic/reflection-only assemblies refuse lookups; keep scanning.
            }
        }

        return null;
    }

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Shape shape = _shape
            ?? throw new InvalidOperationException(
                "host-wire: Apply ran without a captured shape; Applies must be consulted first.");

        harmony.Patch(
            shape.Body,
            prefix: new HarmonyMethod(typeof(HostWireFix), nameof(SerializeJoinResponseBodyPrefix))
            {
                priority = Priority.First
            });

        // Reachability, stated up front so "applied: host-wire" can never be read as "this session used it".
        // The patched method has exactly one caller in the host assembly:
        // dec-official/LanConnectSerializationPatches.cs:411, inside
        // SerializeJoinResponseAtMessageBusPrefix (:397-415), which returns true *before* that call unless
        // LanConnectCompatWirePatches.ShouldForceCompatWireAtMessageBusBoundary() is true (:402-405), and
        // that predicate is true only for Compat4x5V1 (dec-official/LanConnectCompatWirePatches.cs:41-45).
        // tail_v1 therefore never calls it — and in tail_v1 the message is serialized by the game's own
        // ClientLobbyJoinResponseMessage.Serialize instead, which is already the correct 0.113 layout.
        Entry.Info(
            "host-wire: patched " + (shape.Body.DeclaringType?.Name ?? SerializationPatchesTypeName) +
            "." + BodyMethodName + " to emit the 0.113 field layout (gameMode, isCustom).");
        Entry.Info(
            "host-wire: reachability — this repair only affects compat_4x5_v1 rooms. Its target method is " +
            "called from exactly one place, the message-bus boundary prefix, and only when " +
            "ShouldForceCompatWireAtMessageBusBoundary() is true, i.e. only for profile compat_4x5_v1 " +
            "(LanConnectSerializationPatches.cs:397-415, LanConnectCompatWirePatches.cs:41-45). " +
            "tail_v1 rooms never call this method; they serialize the message with the game's own 0.113 " +
            "ClientLobbyJoinResponseMessage.Serialize (plus the host's list-bit-width transpiler). " +
            "So in a tail_v1 session 'applied: host-wire' with no 'emitted the 0.113 join-response body' " +
            "line is the normal, correct outcome — not a silent failure.");
    }

    /// <summary>
    /// Replaces the host mod's 0.111 body. Must stay byte-for-byte equivalent to the game's own
    /// <c>ClientLobbyJoinResponseMessage.Serialize</c> except for the list bit width, which the host mod
    /// widens to support more than four players.
    /// </summary>
    // ReSharper disable once UnusedParameter.Local — Harmony binds by position.
    private static bool SerializeJoinResponseBodyPrefix(
        PacketWriter __0,
        ClientLobbyJoinResponseMessage __1,
        int __2)
    {
        PacketWriter writer = __0;
        ClientLobbyJoinResponseMessage message = __1;
        int lobbyListBitWidth = __2;

        // Both guards, and both messages, are the host mod's own (dec-official/LanConnectSerializationPatches.cs:465-468)
        // so a log line or a stack trace can be compared against the upstream body character for character.
        ArgumentNullException.ThrowIfNull(writer);
        if (message.playersInLobby == null)
        {
            throw new InvalidOperationException(
                "Tried to serialize ClientLobbyJoinResponseMessage with null player list.");
        }

        int startBits = writer.BitPosition;

        writer.WriteList(message.playersInLobby, lobbyListBitWidth);

        writer.WriteBool(message.dailyTime.HasValue);
        if (message.dailyTime.HasValue)
        {
            writer.Write(message.dailyTime.Value);
        }

        writer.WriteBool(message.seed != null);
        if (message.seed != null)
        {
            writer.WriteString(message.seed);
        }

        // The two fields 0.113 added. Omitting them shifts every following field by 4 bits
        // (gameMode 3 + isCustom 1), which is enough to move ascension into the modifiers list header.
        writer.WriteEnum(message.gameMode);
        writer.WriteBool(message.isCustom);

        writer.WriteInt(message.ascension, AscensionBits);
        writer.WriteList(message.modifiers);

        ReportOnce(message, lobbyListBitWidth, writer.BitPosition - startBits);

        return false;
    }

    /// <summary>
    /// Emits one line per session with the numbers a field report needs: the lobby list width actually
    /// used, the body bit count, and the byte count the host mod will log as <c>bodyBytes</c>. Without
    /// it, a silently unpatched host and a patched one can log the same <c>bodyBytes</c> (the missing
    /// four bits often stay inside the same final byte), so the size alone proves nothing.
    /// </summary>
    private static void ReportOnce(ClientLobbyJoinResponseMessage message, int lobbyListBitWidth, int bodyBits)
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0)
        {
            return;
        }

        try
        {
            Entry.Info(
                $"host-wire: emitted the 0.113 join-response body " +
                $"(players={message.playersInLobby?.Count ?? 0}, lobbyListBits={lobbyListBitWidth}, " +
                $"gameMode={message.gameMode}, isCustom={message.isCustom}, ascension={message.ascension}, " +
                $"bodyBits={bodyBits}, expectedBodyBytes={(bodyBits + MessageBusHeaderBits + 7) / 8}). " +
                "Cross-check this against the host mod's 'lobby join-response forced at message-bus boundary' line.");
        }
        catch
        {
            // Diagnostics must never be able to fail a join.
        }
    }
}
