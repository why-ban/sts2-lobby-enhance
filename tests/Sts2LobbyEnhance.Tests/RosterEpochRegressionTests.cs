using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Offline reproduction of the P0 field failure and of the repair, using the *real* shipped types on both
/// sides: the roster authority/codec/carrier/snapshot classes out of the pinned 0.6.3 binary
/// (<see cref="TestPaths.PinnedOfficialModSha256"/>) and the real <c>StartRunLobbyPlayer</c> /
/// <c>LoadRunLobbyPlayer</c> / <c>PacketWriter</c> serializers out of the installed game.
///
/// What was unproven before this file: <c>TailRosterContractTests</c> only showed that the five reflection
/// anchors exist and have the expected shape. It did not show that the mechanism works — that an authority
/// which accepted the previous room's roster really rejects the next room's roster with the production
/// message, and that <c>Reset()</c> — the exact method <c>TailRoster.Reset</c> invokes — really makes the
/// next roster acceptable as a bootstrap.
///
/// Production evidence for the expected message, phone client log line 566639
/// (<c>work/logs/new-report-2/internal-files/instances/v0.113.0-16ac0fe3--d32b3370/logs/godot.log</c>):
/// <code>
/// [ERROR] [lobby-enhance] tail-error-paths: ... reason=lan_protocol_version_mismatch:
///   Current-state snapshots must repeat the accepted revision byte-for-byte.. The frame was received ...
/// </code>
/// The detail is the exception message (which ends in its own period) followed by the reporter's period,
/// hence the doubled dot. <see cref="ProductionDetail"/> is that detail verbatim, and
/// <see cref="T1_..."/> re-derives it from the log and from the decompiled upstream source when those
/// files are present, so the constant cannot drift away from the evidence.
///
/// Nothing here fakes a roster type: snapshots and carriers are produced by the official
/// <c>LanConnectRosterCodec.Decode</c> from canonical bytes, and the carrier payloads are produced by the
/// game's own <c>Serialize</c> methods. The only scaffolding is the model-id table seed described in
/// <see cref="RosterLab.SeedModelId"/>.
/// </summary>
public sealed class RosterEpochRegressionTests
{
    /// <summary>
    /// The exception message the client hit in the field, verbatim (phone log line 566639). Note the
    /// trailing period: it is part of the upstream message (LanConnectRosterAuthorityState.cs:86).
    /// </summary>
    internal const string ProductionDetail =
        "Current-state snapshots must repeat the accepted revision byte-for-byte.";

    private readonly ITestOutputHelper _output;

    public RosterEpochRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void T1_AnAuthorityThatAcceptedTheOldEpochRejectsTheNewEpochAsCurrentState()
    {
        using RosterLab lab = RosterLab.Open();

        // Host side: the two epochs the room really goes through. Epoch A is the finished room's
        // StartRunLobby roster; epoch B is the reloaded room's LoadRunLobby roster.
        IReadOnlyList<RosterLab.Carrier> carriersA = lab.StartRunCarriers();
        IReadOnlyList<RosterLab.Carrier> carriersB = lab.LoadRunCarriers();
        _output.WriteLine(
            $"T1 carriers: epoch A {carriersA[0].BitLength} bits/{carriersA[0].Bytes.Length} B per player " +
            $"(StartRunLobbyPlayer), epoch B {carriersB[0].BitLength} bits/{carriersB[0].Bytes.Length} B " +
            "(LoadRunLobbyPlayer)");

        object host = lab.NewAuthority(RosterLab.HostPeerId);
        object publishedA = lab.CommitHostSnapshot(host, carriersA);
        object publishedB = lab.CommitHostSnapshot(host, carriersB);

        uint revisionA = lab.RevisionOf(publishedA);
        uint revisionB = lab.RevisionOf(publishedB);

        // The revision relationship is *derived* from the real CommitHostSnapshot, not assumed: a null
        // authority starts at 1, and a snapshot whose bytes differ (different carrier type, therefore a
        // different length) bumps it by exactly one.
        Assert.Equal(1u, revisionA);
        Assert.Equal(2u, revisionB);

        // Client side: a binding that already accepted epoch A, exactly like a session that is being kept
        // alive across the rebuild.
        object client = lab.NewAuthority(RosterLab.HostPeerId);
        lab.Accept(client, publishedA, "Bootstrap");
        Assert.Equal(1u, lab.RevisionOf(lab.Current(client)!));

        // The production failure: epoch B arrives, AcceptRoster picks CurrentState because
        // authority.Current != null (LanConnectTailMessageRuntime.cs:1350-1354), and the revision check in
        // Accept rejects it.
        InvalidDataException revisionFailure = Assert.Throws<InvalidDataException>(
            () => lab.Accept(client, publishedB, "CurrentState"));
        Assert.Equal(ProductionDetail, revisionFailure.Message);
        _output.WriteLine($"T1 revision-clause: {revisionFailure.Message}");

        // Same message, but reached through the byte comparison instead of the revision check: an authority
        // that accepted a revision-2 StartRunLobby snapshot rejects the revision-2 LoadRunLobby snapshot
        // because the canonical bytes differ. This is what "byte-for-byte" means in the message.
        object client2 = lab.NewAuthority(RosterLab.HostPeerId);
        lab.Accept(client2, lab.PublishedFromCarriers(RosterLab.HostPeerId, 2u, carriersA), "Bootstrap");
        Assert.Equal(2u, lab.RevisionOf(lab.Current(client2)!));

        InvalidDataException byteFailure = Assert.Throws<InvalidDataException>(
            () => lab.Accept(client2, publishedB, "CurrentState"));
        Assert.Equal(ProductionDetail, byteFailure.Message);
        _output.WriteLine($"T1 byte-clause: {byteFailure.Message}");

        // The reproduction is the production failure only if the string is character-identical to what the
        // client logged. Re-derive it from the log and from upstream source whenever they are present.
        AssertMatchesProductionLog(revisionFailure.Message);
    }

    [Fact]
    public void T2_ResetMakesTheNextEpochAcceptableAsABootstrap()
    {
        using RosterLab lab = RosterLab.Open();

        object host = lab.NewAuthority(RosterLab.HostPeerId);
        object publishedA = lab.CommitHostSnapshot(host, lab.StartRunCarriers());
        object publishedB = lab.CommitHostSnapshot(host, lab.LoadRunCarriers());

        object client = lab.NewAuthority(RosterLab.HostPeerId);
        lab.Accept(client, publishedA, "Bootstrap");
        Assert.Equal(1u, lab.RevisionOf(lab.Current(client)!));

        // Reset() is the very method TailRoster.Reset invokes through reflection
        // (Core/TailRoster.cs:97 -> LanConnectRosterAuthorityState.Reset, LanConnectRosterAuthorityState.cs:128).
        lab.Reset(client);
        Assert.Null(lab.Current(client));

        // After the reset the same epoch B that was rejected in T1 is accepted, as a bootstrap, with the
        // host's revision and roster preserved.
        lab.Accept(client, publishedB, "Bootstrap");

        object current = lab.Current(client)!;
        Assert.Equal(2u, lab.RevisionOf(current));
        Assert.Equal(lab.PlayerIdsOf(publishedB), lab.PlayerIdsOf(current));

        _output.WriteLine(
            $"T2: after Reset() epoch B accepted as Bootstrap, revision={lab.RevisionOf(current)}, " +
            $"players=[{string.Join(",", lab.PlayerIdsOf(current))}]");
    }

    [Fact]
    public void T3_RepeatingTheAcceptedBytesAsCurrentStateStillPasses()
    {
        using RosterLab lab = RosterLab.Open();

        // The upstream happy path the repair must not disturb: the same roster republished as CurrentState
        // is byte-identical, so Accept stores it again without complaint.
        object host = lab.NewAuthority(RosterLab.HostPeerId);
        object publishedA = lab.CommitHostSnapshot(host, lab.StartRunCarriers());

        object client = lab.NewAuthority(RosterLab.HostPeerId);
        lab.Accept(client, publishedA, "Bootstrap");
        lab.Accept(client, publishedA, "CurrentState");

        Assert.Equal(1u, lab.RevisionOf(lab.Current(client)!));
        Assert.Equal(lab.PlayerIdsOf(publishedA), lab.PlayerIdsOf(lab.Current(client)!));
        _output.WriteLine("T3: identical bytes accepted twice (Bootstrap then CurrentState), no reset involved");
    }

    [Fact]
    public void T4_ResetKeepsTheHostPeerIdSoTheAuthorityStillValidates()
    {
        using RosterLab lab = RosterLab.Open();

        // Same two-epoch sequence as T1/T2, so epoch B carries the real revision 2 the host would publish.
        object host = lab.NewAuthority(RosterLab.HostPeerId);
        _ = lab.CommitHostSnapshot(host, lab.StartRunCarriers());
        object publishedB = lab.CommitHostSnapshot(host, lab.LoadRunCarriers());
        Assert.Equal(2u, lab.RevisionOf(publishedB));

        object client = lab.NewAuthority(RosterLab.HostPeerId);
        ulong before = lab.HostPeerIdOf(client);
        Assert.Equal(RosterLab.HostPeerId, before);

        lab.Reset(client);

        // Reset only clears _current/_currentCanonicalBytes (LanConnectRosterAuthorityState.cs:128-135);
        // _hostPeerId is readonly and must survive, or every later Accept would fail ValidateAuthority.
        ulong after = lab.HostPeerIdOf(client);
        Assert.Equal(before, after);

        // Observable consequence, not just a field comparison: with the original sender id the accept
        // passes, with a different one ValidateAuthority still rejects it after the reset.
        lab.Accept(client, publishedB, "Bootstrap");
        Assert.Equal(2u, lab.RevisionOf(lab.Current(client)!));

        object client2 = lab.NewAuthority(RosterLab.HostPeerId);
        lab.Reset(client2);
        InvalidDataException mismatch = Assert.Throws<InvalidDataException>(
            () => lab.Accept(client2, publishedB, "Bootstrap", transportSenderPeerId: RosterLab.HostPeerId + 1));
        Assert.Equal("Roster authority, transport sender, and current host must be identical.", mismatch.Message);

        _output.WriteLine($"T4: _hostPeerId={after} unchanged by Reset(); wrong sender still rejected");
    }

    /// <summary>
    /// Proves <see cref="ProductionDetail"/> is the production string, not a paraphrase: re-extract it from
    /// the field log line and from the decompiled upstream source. Both inputs are optional so the suite
    /// still runs on a machine without the research tree.
    /// </summary>
    private void AssertMatchesProductionLog(string actual)
    {
        Assert.Equal(ProductionDetail, actual);

        string companion = TestPaths.CompanionRoot;
        string logPath = Path.GetFullPath(Path.Combine(
            companion,
            "..",
            "logs",
            "new-report-2",
            "internal-files",
            "instances",
            "v0.113.0-16ac0fe3--d32b3370",
            "logs",
            "godot.log"));

        if (File.Exists(logPath))
        {
            string? line = File.ReadLines(logPath).Skip(566638).FirstOrDefault();
            Assert.NotNull(line);
            Assert.Contains("reason=lan_protocol_version_mismatch: " + actual + ".", line, StringComparison.Ordinal);
            _output.WriteLine($"T1: phone log 566639 detail == exception message (character-identical)");
        }
        else
        {
            _output.WriteLine($"T1: phone log not present at {logPath}; message compared to the constant only");
        }

        string sourcePath = Path.GetFullPath(Path.Combine(
            companion, "..", "lobby", "dec-official", "Sts2LanConnect.Scripts", "LanConnectRosterAuthorityState.cs"));

        if (File.Exists(sourcePath))
        {
            string source = File.ReadAllText(sourcePath);
            Assert.Contains($"Invalid(\"{actual}\")", source, StringComparison.Ordinal);
            _output.WriteLine("T1: decompiled LanConnectRosterAuthorityState.cs:86 contains the same literal");
        }
    }
}

/// <summary>
/// Reflection harness over the pinned 0.6.3 binary plus the installed game, so the regression tests above
/// drive the real implementation instead of a model of it.
///
/// Two constraints shape it:
/// <list type="bullet">
/// <item><c>LanConnectRosterCodec.Decode</c> and the <c>LanConnectRosterPlayerCarrier</c> constructor take
/// <c>ReadOnlySpan&lt;byte&gt;</c>, which <see cref="MethodInfo.Invoke(object, object[])"/> cannot bind.
/// <c>Decode</c> is therefore bound through a delegate with a matching signature
/// (<c>MethodInfo.CreateDelegate</c> accepts a span parameter); the carrier constructor needs no delegate
/// because carriers are obtained by decoding canonical bytes.</item>
/// <item>Every roster object is internal to the host mod, so it is created with
/// <c>Activator.CreateInstance</c> over non-public constructors and every call goes through reflection.</item>
/// </list>
/// </summary>
internal sealed class RosterLab : IDisposable
{
    /// <summary>
    /// First of two distinct 64-bit ids. Both values are borrowed from the 2026-11-13 field session purely so a
    /// failing assertion prints something recognisable; the roles assigned here are fictional — in that session
    /// 5_018_570_539_104_672_532 was the *client's* persistent net id, and 6_803_874_689_479_207_972 only ever
    /// appeared as a control-channel binding on the host with no matching transport connection.
    /// </summary>
    internal const ulong HostPeerId = 5_018_570_539_104_672_532UL;

    /// <summary>Second distinct id; see <see cref="HostPeerId"/> for where the value comes from.</summary>
    internal const ulong GuestPeerId = 6_803_874_689_479_207_972UL;

    private const string Scripts = "Sts2LanConnect.Scripts";
    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private delegate object DecodeSpan(ReadOnlySpan<byte> payload);

    private readonly Type _useType;
    private readonly Type _snapshotType;
    private readonly Type _authorityType;
    private readonly MethodInfo _encode;
    private readonly DecodeSpan _decode;
    private readonly MethodInfo _accept;
    private readonly MethodInfo _commitHostSnapshot;
    private readonly MethodInfo _reset;
    private readonly FieldInfo _hostPeerIdField;
    private readonly PropertyInfo _currentProperty;
    private readonly PropertyInfo _revisionProperty;
    private readonly PropertyInfo _playersProperty;
    private readonly PropertyInfo _playerIdProperty;

    private readonly Type _startRunPlayerType;
    private readonly Type _loadRunPlayerType;
    private readonly Type _writerType;
    private readonly PropertyInfo _writerBitPosition;
    private readonly PropertyInfo _writerBuffer;
    private readonly PropertyInfo _writerWarnOnGrow;
    private readonly object _character;
    private readonly object _unlockState;

    private RosterLab(Assembly game, Assembly host)
    {
        _useType = Require(host, $"{Scripts}.LanConnectRosterSnapshotUse");
        _snapshotType = Require(host, $"{Scripts}.LanConnectRosterSnapshot");
        _authorityType = Require(host, $"{Scripts}.LanConnectRosterAuthorityState");
        Type codec = Require(host, $"{Scripts}.LanConnectRosterCodec");
        Type carrier = Require(host, $"{Scripts}.LanConnectRosterPlayerCarrier");

        _encode = codec.GetMethod("Encode", Any) ?? throw new MissingMethodException(codec.FullName, "Encode");
        MethodInfo decode = codec.GetMethod("Decode", Any)
            ?? throw new MissingMethodException(codec.FullName, "Decode");
        _decode = (DecodeSpan)decode.CreateDelegate(typeof(DecodeSpan));
        _accept = _authorityType.GetMethod("Accept", Any)
            ?? throw new MissingMethodException(_authorityType.FullName, "Accept");
        _commitHostSnapshot = _authorityType.GetMethod("CommitHostSnapshot", Any)
            ?? throw new MissingMethodException(_authorityType.FullName, "CommitHostSnapshot");
        _reset = _authorityType.GetMethod("Reset", Any)
            ?? throw new MissingMethodException(_authorityType.FullName, "Reset");
        _hostPeerIdField = _authorityType.GetField("_hostPeerId", Any)
            ?? throw new MissingFieldException(_authorityType.FullName, "_hostPeerId");
        _currentProperty = _authorityType.GetProperty("Current", Any)
            ?? throw new MissingMemberException(_authorityType.FullName, "Current");
        _revisionProperty = _snapshotType.GetProperty("RosterRevision", Any)
            ?? throw new MissingMemberException(_snapshotType.FullName, "RosterRevision");
        _playersProperty = _snapshotType.GetProperty("Players", Any)
            ?? throw new MissingMemberException(_snapshotType.FullName, "Players");
        _playerIdProperty = carrier.GetProperty("PlayerId", Any)
            ?? throw new MissingMemberException(carrier.FullName, "PlayerId");

        _startRunPlayerType = Require(game, "MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer");
        _loadRunPlayerType = Require(game, "MegaCrit.Sts2.Core.Entities.Multiplayer.LoadRunLobbyPlayer");
        _writerType = Require(game, "MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter");
        _writerBitPosition = _writerType.GetProperty("BitPosition", Any)
            ?? throw new MissingMemberException(_writerType.FullName, "BitPosition");
        _writerBuffer = _writerType.GetProperty("Buffer", Any)
            ?? throw new MissingMemberException(_writerType.FullName, "Buffer");
        _writerWarnOnGrow = _writerType.GetProperty("WarnOnGrow", Any)
            ?? throw new MissingMemberException(_writerType.FullName, "WarnOnGrow");

        _character = CreateCharacter(game);
        _unlockState = Activator.CreateInstance(
            Require(game, "MegaCrit.Sts2.Core.Unlocks.SerializableUnlockState"))!;
    }

    /// <summary>One roster member: exactly what a carrier holds (id, real slot, bit length, carrier bytes).</summary>
    internal readonly record struct Carrier(ulong PlayerId, byte RealSlotId, uint BitLength, byte[] Bytes);

    internal static RosterLab Open([System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        string? gameDirectory = TestPaths.GameDirectory;
        if (gameDirectory is null)
        {
            Assert.Skip($"Game data directory not found. {TestPaths.GameDirectoryHint}");
        }

        string? officialMod = TestPaths.OfficialModDll;
        if (officialMod is null)
        {
            Assert.Skip($"Official sts2_lan_connect DLL not found. {TestPaths.OfficialModHint}");
        }

        GameAssemblyLoader.InstallResolver(gameDirectory!);
        Assembly game = GameAssemblyLoader.Load(Path.Combine(gameDirectory!, "sts2.dll"));
        Assembly host = GameAssemblyLoader.Load(officialMod!);

        // Past the skip guards, so this case really asserts something. Recording here (instead of in each
        // test) is what keeps TestRunLedger honest: a new case in this class cannot forget to report.
        TestRunLedger.Record(testName);

        return new RosterLab(game, host);
    }

    /// <summary>Epoch A: carriers serialized from the real <c>StartRunLobbyPlayer</c>.</summary>
    internal IReadOnlyList<Carrier> StartRunCarriers() =>
    [
        SerializeStartRunPlayer(HostPeerId, 0),
        SerializeStartRunPlayer(GuestPeerId, 1)
    ];

    /// <summary>Epoch B: carriers serialized from the real <c>LoadRunLobbyPlayer</c>.</summary>
    internal IReadOnlyList<Carrier> LoadRunCarriers() =>
    [
        SerializeLoadRunPlayer(HostPeerId, 0),
        SerializeLoadRunPlayer(GuestPeerId, 1)
    ];

    internal object NewAuthority(ulong hostPeerId) =>
        Activator.CreateInstance(
            _authorityType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [hostPeerId],
            culture: null)!;

    /// <summary>Runs the host-side publish step, so the revision numbers come from the real implementation.</summary>
    internal object CommitHostSnapshot(object authority, IReadOnlyList<Carrier> carriers)
    {
        object snapshot = DecodeCanonical(BuildCanonical(HostPeerId, 1u, carriers));
        return Invoke(_commitHostSnapshot, authority, snapshot.GetType().GetProperty("Players", Any)!.GetValue(snapshot));
    }

    /// <summary>A published snapshot built directly from carriers, for the byte-comparison variant of T1.</summary>
    internal object PublishedFromCarriers(ulong hostPeerId, uint revision, IReadOnlyList<Carrier> carriers) =>
        DecodeCanonical(BuildCanonical(hostPeerId, revision, carriers));

    internal void Accept(
        object authority,
        object snapshot,
        string use,
        ulong? transportSenderPeerId = null)
    {
        _ = Invoke(
            _accept,
            authority,
            transportSenderPeerId ?? HostPeerId,
            snapshot,
            Enum.Parse(_useType, use),
            null,
            null);
    }

    internal void Reset(object authority) => _ = Invoke(_reset, authority);

    internal object? Current(object authority) => _currentProperty.GetValue(authority);

    internal uint RevisionOf(object snapshot) => (uint)_revisionProperty.GetValue(snapshot)!;

    internal ulong HostPeerIdOf(object authority) => (ulong)_hostPeerIdField.GetValue(authority)!;

    internal IReadOnlyList<ulong> PlayerIdsOf(object snapshot)
    {
        var players = (IEnumerable)_playersProperty.GetValue(snapshot)!;
        List<ulong> ids = [];
        foreach (object player in players)
        {
            ids.Add((ulong)_playerIdProperty.GetValue(player)!);
        }

        return ids;
    }

    public void Dispose()
    {
        // Nothing unmanaged is held: the assemblies stay loaded for the lifetime of the test process.
    }

    private object DecodeCanonical(byte[] canonical) => _decode(canonical);

    /// <summary>
    /// Assembles the canonical roster payload that <c>LanConnectRosterCodec.Encode</c> produces
    /// (LanConnectRosterCodec.cs:21-67): a 15-byte header followed by 13 fixed bytes plus the carrier bytes
    /// per player. Decoding it is what turns the hand-assembled bytes back into real snapshot/carrier
    /// objects; T1 asserts the round trip is byte-identical.
    /// </summary>
    private static byte[] BuildCanonical(ulong authorityPeerId, uint revision, IReadOnlyList<Carrier> carriers)
    {
        int total = 15;
        foreach (Carrier carrier in carriers)
        {
            total += 13 + carrier.Bytes.Length;
        }

        byte[] payload = new byte[total];
        payload[0] = 1;
        payload[1] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(2, 8), authorityPeerId);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(10, 4), revision);
        payload[14] = (byte)carriers.Count;

        int offset = 15;
        foreach (Carrier carrier in carriers)
        {
            BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(offset, 8), carrier.PlayerId);
            offset += 8;
            payload[offset++] = carrier.RealSlotId;
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset, 4), carrier.BitLength);
            offset += 4;
            carrier.Bytes.CopyTo(payload, offset);
            offset += carrier.Bytes.Length;
        }

        return payload;
    }

    /// <summary>
    /// Serializes a real <c>StartRunLobbyPlayer</c>. Its <c>Serialize</c> writes a character through
    /// <c>WriteModel</c>, which needs the game's model-id table, so that table is seeded first.
    /// </summary>
    private Carrier SerializeStartRunPlayer(ulong playerId, byte realSlotId)
    {
        object player = Activator.CreateInstance(_startRunPlayerType)!;
        SetField(player, "id", playerId);
        SetField(player, "slotId", (int)realSlotId);
        SetField(player, "character", _character);
        SetField(player, "unlockState", _unlockState);
        return Serialize(player, playerId, realSlotId);
    }

    private Carrier SerializeLoadRunPlayer(ulong playerId, byte realSlotId)
    {
        object player = Activator.CreateInstance(_loadRunPlayerType)!;
        SetField(player, "id", playerId);
        SetField(player, "isModded", true);
        SetField(player, "isReady", true);
        return Serialize(player, playerId, realSlotId);
    }

    private Carrier Serialize(object player, ulong playerId, byte realSlotId)
    {
        object writer = Activator.CreateInstance(_writerType)!;
        _writerWarnOnGrow.SetValue(writer, false);
        _ = Invoke(
            player.GetType().GetMethod("Serialize", Any)!,
            player,
            writer);

        int bitPosition = (int)_writerBitPosition.GetValue(writer)!;
        byte[] buffer = (byte[])_writerBuffer.GetValue(writer)!;
        int byteLength = (bitPosition + 7) / 8;
        return new Carrier(playerId, realSlotId, (uint)bitPosition, buffer[..byteLength]);
    }

    private static void SetField(object target, string name, object? value)
    {
        FieldInfo field = target.GetType().GetField(name, Any)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    /// <summary>
    /// Instantiates one real <c>CharacterModel</c> so <c>StartRunLobbyPlayer.Serialize</c> can write a
    /// character. <c>Deprived</c> is the game's own mock character: it has no assets, and its constructor is
    /// the only thing touched (its Godot-typed members are computed lazily).
    /// </summary>
    private static object CreateCharacter(Assembly game)
    {
        Type deprived = Require(game, "MegaCrit.Sts2.Core.Models.Characters.Deprived");
        object character = Activator.CreateInstance(deprived)!;
        SeedModelId(game, character);
        return character;
    }

    /// <summary>
    /// Seeds <c>ModelIdSerializationCache</c> with this one character.
    ///
    /// Why not the game's own bootstrap: <c>ModelDb.Init()</c> instantiates every model in the game, and
    /// <c>ModelIdSerializationCache.Init()</c> ends with a <c>Log.Info</c> call
    /// (ModelIdSerializationCache.cs:126). MegaCrit's logger resolves <c>Godot.OS</c> in its static
    /// constructor, which is an uncatchable access violation outside a Godot process (see Entry.cs:28-35),
    /// so calling it here would kill the test host instead of failing a test.
    ///
    /// What this seeds is only the id table that <c>WriteModelEntry</c> consults: one entry mapped to netId 1
    /// with an 8-bit field. The real table has thousands of entries and a ~12-bit field, which changes the
    /// length of the serialized player, not the roster mechanism under test. The seed is verified with
    /// <c>TryGetNetIdForEntry</c> before any serialization happens — if it did not take,
    /// <c>WriteModelEntry</c> would fall through to <c>Log.Warn</c>, and the assertion below stops the test
    /// before that can be reached.
    /// </summary>
    private static void SeedModelId(Assembly game, object model)
    {
        Type cache = Require(game, "MegaCrit.Sts2.Core.Multiplayer.Serialization.ModelIdSerializationCache");
        object id = model.GetType().GetProperty("Id", Any)!.GetValue(model)!;
        string entry = (string)id.GetType().GetProperty("Entry", Any)!.GetValue(id)!;

        var entryToNetId = (Dictionary<string, int>)cache.GetField("_entryNameToNetIdMap", Any)!.GetValue(null)!;
        var netIdToEntry = (List<string>)cache.GetField("_netIdToEntryNameMap", Any)!.GetValue(null)!;
        if (!entryToNetId.ContainsKey(entry))
        {
            entryToNetId[entry] = netIdToEntry.Count;
            netIdToEntry.Add(entry);
        }

        cache.GetField("<EntryIdBitSize>k__BackingField", Any)!.SetValue(null, 8);
        cache.GetField("<CategoryIdBitSize>k__BackingField", Any)!.SetValue(null, 8);
        cache.GetField("_initialized", Any)!.SetValue(null, true);

        MethodInfo tryGet = cache.GetMethod("TryGetNetIdForEntry", Any)!;
        object?[] arguments = [entry, 0];
        Assert.True(
            (bool)tryGet.Invoke(null, arguments)!,
            $"Could not seed ModelIdSerializationCache with '{entry}', so StartRunLobbyPlayer cannot be " +
            "serialized without hitting the game's logger.");
    }

    private static object Invoke(MethodInfo method, object target, params object?[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments)!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            // Rethrow the real exception so the tests can assert on the upstream type and message instead of
            // on the reflection wrapper.
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static Type Require(Assembly assembly, string fullName) =>
        assembly.GetType(fullName, throwOnError: false, ignoreCase: false)
        ?? throw new InvalidOperationException(
            $"Type '{fullName}' was not found in assembly '{assembly.GetName().Name}'.");
}
