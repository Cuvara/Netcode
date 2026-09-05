using System.Collections.Generic;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    public sealed class AbilityProtocolTests
    {
        [Test]
        public void CastAbilityInput_StoresFields()
        {
            var input = new CastAbilityInput
            {
                Tick = 100,
                AbilityId = "fireball",
                TargetId = "mob-1",
                TargetX = 10f,
                TargetY = 5f,
            };
            Assert.AreEqual(100UL, input.Tick);
            Assert.AreEqual("fireball", input.AbilityId);
            Assert.AreEqual("mob-1", input.TargetId);
        }

        [Test]
        public void AbilityEvent_AllResultsExist()
        {
            Assert.AreEqual(0, (int)AbilityCastResult.Hit);
            Assert.AreEqual(1, (int)AbilityCastResult.Miss);
            Assert.AreEqual(2, (int)AbilityCastResult.OutOfRange);
            Assert.AreEqual(3, (int)AbilityCastResult.OnCooldown);
            Assert.AreEqual(4, (int)AbilityCastResult.InvalidTarget);
            Assert.AreEqual(5, (int)AbilityCastResult.CasterIncapacitated);
            Assert.AreEqual(6, (int)AbilityCastResult.InsufficientResource);
        }

        [Test]
        public void StatusEffect_DefaultStacks_IsOne()
        {
            var effect = new StatusEffect { EffectId = "poison" };
            Assert.AreEqual(1, effect.Stacks);
        }

        [Test]
        public void SnapshotExtensions_SetAndGetEffects()
        {
            var ext = new SnapshotExtensions();
            var effects = new List<StatusEffect>
            {
                new StatusEffect { EffectId = "poison", RemainingTicks = 30, Stacks = 2 },
                new StatusEffect { EffectId = "slow", RemainingTicks = 15 },
            };

            ext.SetEffects("mob-1", effects);

            var result = ext.GetEffects("mob-1");
            Assert.IsNotNull(result);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("poison", result[0].EffectId);
            Assert.AreEqual(2, result[0].Stacks);
        }

        [Test]
        public void SnapshotExtensions_RemoveEntity_ClearsEffects()
        {
            var ext = new SnapshotExtensions();
            ext.SetEffects("mob-1", new List<StatusEffect> { new StatusEffect { EffectId = "burn" } });

            ext.RemoveEntity("mob-1");

            Assert.IsNull(ext.GetEffects("mob-1"));
        }

        [Test]
        public void SnapshotExtensions_AbilityEvents_OneShotPerTick()
        {
            var ext = new SnapshotExtensions();
            ext.AddAbilityEvent(new AbilityEvent { AbilityId = "fireball", CasterId = "p1", Result = AbilityCastResult.Hit });
            ext.AddAbilityEvent(new AbilityEvent { AbilityId = "heal", CasterId = "p2", Result = AbilityCastResult.Hit });

            Assert.AreEqual(2, ext.AbilityEvents.Count);

            ext.ClearAbilityEvents();
            Assert.AreEqual(0, ext.AbilityEvents.Count);
        }

        [Test]
        public void SnapshotExtensions_Reset_ClearsEverything()
        {
            var ext = new SnapshotExtensions();
            ext.SetEffects("mob-1", new List<StatusEffect> { new StatusEffect { EffectId = "burn" } });
            ext.AddAbilityEvent(new AbilityEvent { AbilityId = "fireball" });

            ext.Reset();

            Assert.AreEqual(0, ext.AbilityEvents.Count);
            Assert.IsNull(ext.GetEffects("mob-1"));
        }

        [Test]
        public void SnapshotExtensions_EmptyEffects_RemovesEntry()
        {
            var ext = new SnapshotExtensions();
            ext.SetEffects("mob-1", new List<StatusEffect> { new StatusEffect { EffectId = "burn" } });
            ext.SetEffects("mob-1", null); // clear

            Assert.IsNull(ext.GetEffects("mob-1"));
        }
    }
}
