using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Puts the host's lobby list back into slot order right before the begin-run message is built.
///
/// The seed of every player is <c>hash(seed) + RunState.GetPlayerSlotIndex(player)</c>, and that index is simply
/// <c>Players.IndexOf(player)</c> in each side's own list. Upstream's tail layer replaces the message body with a
/// list sorted by <c>(slotId, id)</c> but leaves the host's own list untouched, so after anybody leaves and comes
/// back the host runs in join order while every client runs in slot order, and the first player to compare seeds
/// raises "RngSet seed should not change during the run!".
///
/// Sorting here uses the same key the tail layer uses, which makes its reorder an identity: a patched host and an
/// unpatched client agree, and an unpatched host with a patched client is untouched. The load path is a different
/// one (<c>RestoreLoadJoinPlayers</c>) and is handled by <see cref="LoadJoinOrderFix"/>.
/// </summary>
internal sealed class BeginRunOrderFix : ICompatFix
{
    private const string LobbyTypeName = "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby";
    private const string PlayerTypeName = "MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer";
    private const string BeginMethodName = "BeginRunForAllPlayers";
    private const string PlayersPropertyName = "Players";

    private static Shape? _shape;
    private static int _logged;

    public string Id => "begin-run-order";

    public FixGroup Group => FixGroup.Roster;

    public bool Applies(out string reason)
    {
        _shape = Shape.TryCapture(out reason);
        return _shape != null;
    }

    public void Apply(Harmony harmony)
    {
        Shape shape = _shape ?? throw new InvalidOperationException("begin-run-order: the shape was not captured.");
        harmony.Patch(
            shape.Begin,
            prefix: new HarmonyMethod(typeof(BeginRunOrderFix), nameof(BeginPrefix))
            {
                priority = Priority.First
            });

        Entry.Info("begin-run-order: the host's lobby list is put back into slot order before the begin-run message.");
    }

    private static bool BeginPrefix(object __instance)
    {
        try
        {
            Shape shape = _shape ?? throw new InvalidOperationException("begin-run-order: the shape was not captured.");
            if (shape.Players.GetValue(__instance) is not IList players || players.Count < 2)
            {
                return true;
            }

            int count = players.Count;
            var order = new (int Slot, ulong Id, object Item)[count];
            bool sorted = true;
            for (int index = 0; index < count; index++)
            {
                object item = players[index]!;
                int slot = (int)shape.Slot.GetValue(item)!;
                ulong id = (ulong)shape.IdOf.GetValue(item)!;
                order[index] = (slot, id, item);
                if (index > 0 && Compare(order[index - 1], order[index]) > 0)
                {
                    sorted = false;
                }
            }

            if (sorted)
            {
                return true;
            }

            Array.Sort(order, static (left, right) => Compare(left, right));
            for (int index = 0; index < count; index++)
            {
                players[index] = order[index].Item;
            }

            if (Interlocked.Exchange(ref _logged, 1) == 0)
            {
                Entry.Info("begin-run-order: host lobby list re-ordered to slot order (" + Describe(order) + ").");
            }
        }
        catch (Exception exception)
        {
            Entry.Warn("begin-run-order: the host lobby list could not be re-ordered, so the run begins in join " +
                       $"order as before: {exception.GetType().Name}: {exception.Message}");
        }

        return true;
    }

    private static int Compare((int Slot, ulong Id, object Item) left, (int Slot, ulong Id, object Item) right) =>
        left.Slot != right.Slot ? left.Slot.CompareTo(right.Slot) : left.Id.CompareTo(right.Id);

    private static string Describe((int Slot, ulong Id, object Item)[] order) =>
        string.Join(",", order.Select(entry => entry.Slot + ":" + entry.Id));

    internal sealed class Shape
    {
        internal MethodInfo Begin = null!;
        internal PropertyInfo Players = null!;
        internal FieldInfo Slot = null!;
        internal FieldInfo IdOf = null!;

        internal static Shape? TryCapture(out string reason)
        {
            var missing = new List<string>();
            Type? lobby = ReflectScope.GameType(LobbyTypeName, missing);
            Type? player = ReflectScope.GameType(PlayerTypeName, missing);

            MethodInfo? begin = null;
            PropertyInfo? players = null;
            if (lobby != null)
            {
                begin = lobby.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                         BindingFlags.NonPublic)
                    .FirstOrDefault(method => method.Name == BeginMethodName
                                              && method.GetParameters().Length == 2
                                              && method.GetParameters()[0].ParameterType == typeof(string)
                                              && method.GetParameters()[1].ParameterType.Name == "List`1");
                if (begin == null)
                {
                    missing.Add(LobbyTypeName + "." + BeginMethodName + "(string, List<>)");
                }

                players = lobby.GetProperty(PlayersPropertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (players == null || players.PropertyType.Name != "List`1")
                {
                    missing.Add(LobbyTypeName + "." + PlayersPropertyName + " (List<>)");
                }
            }

            FieldInfo? slot = null;
            FieldInfo? id = null;
            if (player != null)
            {
                slot = player.GetField("slotId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (slot == null || slot.FieldType != typeof(int))
                {
                    missing.Add(PlayerTypeName + ".slotId (int)");
                }

                id = player.GetField("id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (id == null || id.FieldType != typeof(ulong))
                {
                    missing.Add(PlayerTypeName + ".id (ulong)");
                }
            }

            if (missing.Count > 0)
            {
                reason = "missing: " + string.Join(", ", missing);
                return null;
            }

            reason = string.Empty;
            return new Shape { Begin = begin!, Players = players!, Slot = slot!, IdOf = id! };
        }
    }
}
