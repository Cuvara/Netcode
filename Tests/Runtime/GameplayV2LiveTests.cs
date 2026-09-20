using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.Transport;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace Cuvara.Netcode.Tests.PlayMode
{
    /// <summary>
    /// The gameplay-v2 wire additions from a REAL Unity client against a REAL game server:
    /// the event channel, the ability input, and the action_seq retrigger counter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists when the Go integration test already covers the same wire.</b> That
    /// test proves the SERVER emits the fields. This one proves this package's own transport,
    /// codec, resolver and merger deliver them to a consumer — a different half of the same
    /// contract, and the half a player actually runs. The two together are what "end to end"
    /// means; either alone is a half that was green while the other half was missing, which is
    /// how this feature already shipped broken once.
    /// </para>
    /// <para>
    /// <b>The gateway and Nakama are deliberately skipped.</b> The two-hop handshake is covered
    /// end to end by the backend's own integration suite, and by <c>E2ECertification</c>. What
    /// is under test here is what arrives once a client is in the world, so the harness is
    /// handed a join token minted out of band and connects straight to the game server.
    /// </para>
    /// <para>
    /// <b>Skipped unless pointed at a server.</b> Set <c>CUVARA_LIVE_GS_ADDR</c> and
    /// <c>CUVARA_LIVE_JOIN_TOKEN</c>. A skip is reported as a skip rather than a pass: a test
    /// that silently passes when it did not run is worse than no test.
    /// </para>
    /// </remarks>
    [Category("LiveBackend")]
    public class GameplayV2LiveTests
    {
        private const string AddrVar = "CUVARA_LIVE_GS_ADDR";
        private const string TokenVar = "CUVARA_LIVE_JOIN_TOKEN";

        /// <summary>Content id of the entity-targeted damage ability the server was started with.</summary>
        private const uint BoltAbility = 1;

        private static string Addr => Environment.GetEnvironmentVariable(AddrVar);

        /// <summary>
        /// Join tokens, comma separated, one per test.
        /// </summary>
        /// <remarks>
        /// <b>A join token is single-use</b> — the server refuses a second join with
        /// "Token already used", which is a replay defence, not a bug. Two tests sharing one
        /// token therefore fail the second one for a reason that has nothing to do with what
        /// it is testing, and the harness has to supply as many as it runs.
        /// </remarks>
        private static string[] Tokens
        {
            get
            {
                string raw = Environment.GetEnvironmentVariable(TokenVar);
                return string.IsNullOrEmpty(raw)
                    ? Array.Empty<string>()
                    : raw.Split(',');
            }
        }

        private static int _tokenCursor;

        private static string NextToken()
        {
            var all = Tokens;
            int i = Interlocked.Increment(ref _tokenCursor) - 1;
            Assert.That(i, Is.LessThan(all.Length),
                $"ran out of join tokens: {all.Length} supplied, test {i + 1} asked for one. " +
                "A join token is single-use; supply one per test.");
            return all[i];
        }

        private static void RequireTarget()
        {
            if (string.IsNullOrEmpty(Addr) || Tokens.Length == 0)
            {
                Assert.Ignore(
                    $"no live game server configured. Set {AddrVar} (host:port) and {TokenVar} " +
                    "to run this against a real server.");
            }
        }

        /// <summary>Collects everything a consumer would see, exactly as a consumer sees it.</summary>
        private sealed class Observer
        {
            public readonly List<ResolvedGameEvent> Events = new List<ResolvedGameEvent>();
            public readonly Dictionary<string, ResolvedEntity> Entities =
                new Dictionary<string, ResolvedEntity>();
            public int Snapshots;

            public void OnSnapshot(ResolvedSnapshot snapshot)
            {
                Snapshots++;

                if (snapshot.Full) Entities.Clear();
                foreach (var e in snapshot.Entities) Entities[e.Id] = e;
                foreach (var id in snapshot.Removed) Entities.Remove(id);

                // Appended, never merged: events are not state. They belong to the tick that
                // produced them and are never re-sent, so a consumer reads them once.
                Events.AddRange(snapshot.Events);
            }

            public IEnumerable<ResolvedGameEvent> Of(GameEventType kind)
            {
                foreach (var e in Events)
                {
                    if (e.Type == kind) yield return e;
                }
            }
        }

        private static (GameSessionClient client, Observer observer) Connect()
        {
            var parts = Addr.Split(':');
            Assert.That(parts.Length, Is.EqualTo(2), $"{AddrVar} must be host:port, got '{Addr}'");

            var client = new GameSessionClient(
                new NetworkSettings(), new DefaultTransportFactory(),
                new ProtobufWireCodec(), new UnityNetLog());

            var observer = new Observer();
            client.SnapshotReceived += observer.OnSnapshot;
            return (client, observer);
        }

        private static async UniTask JoinAsync(GameSessionClient client, CancellationToken ct)
        {
            var parts = Addr.Split(':');
            var assignment = new MapAssignment(
                new NetworkEndpoint(parts[0], int.Parse(parts[1])),
                NextToken(),
                TransportKind.Tcp);

            await client.JoinAsync(assignment, ct);
        }

        /// <summary>Pumps for a while, letting snapshots arrive on the main thread.</summary>
        private static async UniTask PumpAsync(float seconds, CancellationToken ct)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (Time.realtimeSinceStartupAsDouble < until)
            {
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
        }

        /// <summary>
        /// Brings up a second real client to be hit.
        /// </summary>
        /// <remarks>
        /// <b>A second PLAYER rather than one of the server's mobs, and the reason is that a
        /// mob runs away.</b> The first version of this test hunted the nearest mob: mobs carry
        /// server-side AI, the AOI radius is far wider than any attack range, and the chase
        /// ended 20 units short — which surfaced as "no damage event", the same symptom as the
        /// event channel being broken. Players do not move unless their own client tells them
        /// to, and both spawn at the map's spawn point, so they are in range from the first
        /// snapshot and the result cannot be a positioning accident.
        /// </remarks>
        private static async UniTask<(GameSessionClient client, Observer observer)> JoinVictimAsync(
            CancellationToken ct)
        {
            var (victim, victimObserver) = Connect();
            await JoinAsync(victim, ct);
            await PumpAsync(0.5f, ct);
            return (victim, victimObserver);
        }

        /// <summary>Distance between two entities, squared. No Sqrt on a diagnostic path.</summary>
        private static float DistanceSq(in ResolvedEntity a, in ResolvedEntity b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return (dx * dx) + (dy * dy);
        }

        [UnityTest]
        public IEnumerator AnAttackDeliversADamageEventAndAdvancesTheRetriggerCounter() =>
            UniTask.ToCoroutine(async () =>
            {
                RequireTarget();

                var (client, observer) = Connect();
                using (client)
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    await JoinAsync(client, cts.Token);
                    Assert.That(client.UserId, Is.Not.Empty, "joined without a user id");
                    Debug.Log($"[GameplayV2Live] joined as {client.UserId}, tick_rate={client.TickRate}");

                    await PumpAsync(1.5f, cts.Token);
                    Assert.That(observer.Snapshots, Is.GreaterThan(0), "no snapshot arrived");

                    var (victim, _) = await JoinVictimAsync(cts.Token);
                    using var victimScope = victim;
                    await PumpAsync(1.0f, cts.Token);

                    string mob = victim.UserId;
                    Assert.That(observer.Entities.ContainsKey(mob), Is.True,
                        $"the attacker cannot see {mob}; the two players are not in each " +
                        "other's AOI, so nothing below would be testing combat");

                    float sq = DistanceSq(observer.Entities[client.UserId], observer.Entities[mob]);
                    Assert.That(sq, Is.LessThanOrEqualTo(9f),
                        $"the two players are {Mathf.Sqrt(sq):F2} units apart, outside the 3.0 " +
                        "attack range. An out-of-range refusal presents as 'no damage event', " +
                        "which is the same symptom as the channel being broken.");

                    long tick = 0;
                    observer.Events.Clear();
                    client.SendInput(++tick, 0f, 0f, mob);
                    await PumpAsync(1.5f, cts.Token);

                    var damage = new List<ResolvedGameEvent>(observer.Of(GameEventType.Damage));
                    Assert.That(damage, Is.Not.Empty,
                        "no damage event reached this client after an attack. HP is state and a " +
                        "hit is an occurrence: without this channel a client cannot tell a hit " +
                        "from a heal that netted out in the same tick.");

                    Assert.That(damage[0].Amount, Is.GreaterThan(0));
                    Assert.That(damage[0].TargetId, Is.EqualTo(mob),
                        "the resolver did not turn the event's interned handle back into an id");
                    Assert.That(damage[0].SourceId, Is.EqualTo(client.UserId));
                    Debug.Log($"[GameplayV2Live] damage {damage[0].SourceId} -> {damage[0].TargetId} " +
                              $"amount={damage[0].Amount}");

                    var meAfter = observer.Entities[client.UserId];
                    Assert.That(meAfter.Action, Is.EqualTo(SimAction.Attacking));
                    Assert.That(meAfter.ActionSeq, Is.Not.Zero,
                        "action_seq is 0 after an accepted attack, which a client cannot tell " +
                        "from a server that does not send the field at all");
                    uint firstSeq = meAfter.ActionSeq;

                    // A second swing: identical position, identical action. Only the counter
                    // moves, and if the delta encoder did not consider it the entity would be
                    // suppressed entirely and the swing lost.
                    await PumpAsync(0.8f, cts.Token);
                    observer.Events.Clear();
                    client.SendInput(++tick, 0f, 0f, mob);
                    await PumpAsync(1.5f, cts.Token);

                    Assert.That(new List<ResolvedGameEvent>(observer.Of(GameEventType.Damage)),
                        Is.Not.Empty, "second attack produced no damage event");

                    uint secondSeq = observer.Entities[client.UserId].ActionSeq;
                    Assert.That(secondSeq, Is.Not.EqualTo(firstSeq),
                        "action_seq did not change between two attacks; a renderer has no other " +
                        "way to learn the second swing happened");
                    Debug.Log($"[GameplayV2Live] action_seq {firstSeq} -> {secondSeq}");

                    client.Leave();
                }
            });

        [UnityTest]
        public IEnumerator AnAbilityResolvesAndReportsItself() =>
            UniTask.ToCoroutine(async () =>
            {
                RequireTarget();

                var (client, observer) = Connect();
                using (client)
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    await JoinAsync(client, cts.Token);
                    await PumpAsync(1.5f, cts.Token);

                    var (victim, _) = await JoinVictimAsync(cts.Token);
                    using var victimScope = victim;
                    await PumpAsync(1.0f, cts.Token);

                    string mob = victim.UserId;
                    Assert.That(observer.Entities.ContainsKey(mob), Is.True,
                        $"the caster cannot see {mob}");

                    long tick = 0;
                    observer.Events.Clear();
                    client.SendAbilityInput(++tick, 0f, 0f, BoltAbility, abilityTargetId: mob);
                    await PumpAsync(1.5f, cts.Token);

                    var casts = new List<ResolvedGameEvent>(observer.Of(GameEventType.AbilityCast));
                    Assert.That(casts, Is.Not.Empty,
                        "no ability_cast event. Abilities are not predicted, so this event is the " +
                        "ONLY report a client gets that its cast resolved — a UI driving a " +
                        "cooldown sweep off the input alone would be lying to the player.");
                    Assert.That(casts[0].AbilityId, Is.EqualTo(BoltAbility));
                    Assert.That(casts[0].SourceId, Is.EqualTo(client.UserId));

                    var damage = new List<ResolvedGameEvent>(observer.Of(GameEventType.Damage));
                    Assert.That(damage, Is.Not.Empty, "the ability cast but dealt no damage");
                    Assert.That(damage[0].AbilityId, Is.EqualTo(BoltAbility),
                        "a damage number must be attributable to the ability that caused it");

                    Debug.Log($"[GameplayV2Live] bolt -> {damage[0].TargetId} amount={damage[0].Amount}");

                    client.Leave();
                }
            });
    }
}
