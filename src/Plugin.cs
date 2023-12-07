using System;
using BepInEx;
using UnityEngine;
using SlugBase.Features;
using static SlugBase.Features.FeatureTypes;
using MoreSlugcats;
using Random = UnityEngine.Random;
using MonoMod.RuntimeDetour;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using BepInEx.Logging;

namespace SlugTemplate
{
    [BepInPlugin(MOD_ID, "Kill Me Now RW", "0.1.0")]
    class Plugin : BaseUnityPlugin
    {
        private const string MOD_ID = "bottles.killmenowrw";
        private const float SLUP_EXPLODE_CHANCE = 0.1f; // chance to explode when abs(slugpup food pref) = 1
        private const float MINOR_ELEC_DEATH_AMOUNT = 0.02f; // 50% is where it becomes lethal; don't set it to that
        private const float CENTIPEDE_EXTEND_CHANCE = 0.99f;
        private const float LIZARD_EXTEND_CHANCE = 0.99f;

        public static RemixMenu Options;
        public static ManualLogSource logger;

        public static readonly PlayerFeature<float> SuperJump = PlayerFloat("slugtemplate/super_jump");
        public static readonly PlayerFeature<bool> ExplodeOnDeath = PlayerBool("slugtemplate/explode_on_death");
        public static readonly GameFeature<float> MeanLizards = GameFloat("slugtemplate/mean_lizards");

        public Plugin()
        {
            logger = base.Logger;
            Options = new RemixMenu(this);
        }


        // Add hooks
        public void OnEnable()
        {
            try
            {
                On.RainWorld.OnModsInit += Extras.WrapInit(LoadResources);
                On.RainWorld.OnModsInit += SetUpRemixMenu;

                // Slugbase hooks
                On.Player.Jump += Player_Jump;
                On.Player.Die += Player_Die;
                On.Lizard.ctor += Lizard_ctor;

                // Slugpup shenanigans :monksilly:
                On.Player.SpitOutOfShortCut += SlupsSpawnInPipes_Hook;
                On.Player.ObjectEaten += SlupExplodeOnEat_Hook;

                // Iterator related things
                On.OracleBehavior.Update += OracleBehavior_Update;

                // Electric death in every room (except shelters) all throughout the cycle, but minor enough that it doesn't kill until actually time to do stuff
                On.Room.Loaded += AddMinorElectricDeath_Hook;
                new Hook(typeof(ElectricDeath).GetProperty(nameof(ElectricDeath.Intensity))!.GetGetMethod(), ElectricDeath_Intensity_get_Hook);

                // creatures explode like a sinularity when they die
                On.Creature.Die += Creature_BlackHoleOnDeath;

                // Touching neuron flies kills you :monkdevious:
                On.PhysicalObject.Collide += NeuronFliesKill_Hook;

                // Absurdly long creatures
                IL.Centipede.ctor += LongCentipedes_Hook;

                IL.Lizard.ctor += LongLizards_Hook1;
                // todo: hook in Lizard.Update? (line 442)
                IL.LizardAI.Update += LongLizards_Hook2;
                IL.LizardGraphics.Update += LongLizards_Hook3;
                IL.LizardGraphics.UpdateTailSegment += LongLizards_Hook4;

                Logger.LogMessage("Successfully loaded");
            }
            catch (Exception e)
            {
                Logger.LogError("Failed to load!");
                Logger.LogError(e);
            }
        }

        private void SetUpRemixMenu(On.RainWorld.orig_OnModsInit orig, RainWorld self)
        {
            orig(self);
            MachineConnector.SetRegisteredOI(MOD_ID, Options);
        }

        // Load any resources, such as sprites or sounds
        private void LoadResources(RainWorld rainWorld)
        {
        }


        #region slugbase default hooks

        // Implement MeanLizards
        private void Lizard_ctor(On.Lizard.orig_ctor orig, Lizard self, AbstractCreature abstractCreature, World world)
        {
            orig(self, abstractCreature, world);

            if (MeanLizards.TryGet(world.game, out float meanness))
            {
                self.spawnDataEvil = Mathf.Min(self.spawnDataEvil, meanness);
            }
        }


        // Implement SuperJump
        private void Player_Jump(On.Player.orig_Jump orig, Player self)
        {
            orig(self);

            if (SuperJump.TryGet(self, out var power))
            {
                self.jumpBoost *= 1f + power;
            }
        }

        // Implement ExlodeOnDeath
        private void Player_Die(On.Player.orig_Die orig, Player self)
        {
            bool wasDead = self.dead;

            orig(self);

            if (!wasDead && self.dead
                && ExplodeOnDeath.TryGet(self, out bool explode)
                && explode)
            {
                // Adapted from ScavengerBomb.Explode
                var room = self.room;
                var pos = self.mainBodyChunk.pos;
                var color = self.ShortCutColor();
                room.AddObject(new Explosion(room, self, pos, 7, 250f, 6.2f, 2f, 280f, 0.25f, self, 0.7f, 160f, 1f));
                room.AddObject(new Explosion.ExplosionLight(pos, 280f, 1f, 7, color));
                room.AddObject(new Explosion.ExplosionLight(pos, 230f, 1f, 3, new Color(1f, 1f, 1f)));
                room.AddObject(new ExplosionSpikes(room, pos, 14, 30f, 9f, 7f, 170f, color));
                room.AddObject(new ShockWave(pos, 330f, 0.045f, 5, false));

                room.ScreenMovement(pos, default, 1.3f);
                room.PlaySound(SoundID.Bomb_Explode, pos);
                room.InGameNoise(new Noise.InGameNoise(pos, 9000f, self, 1f));
            }
        }

        #endregion

        #region slugpup stuff

        private void SlupExplodeOnEat_Hook(On.Player.orig_ObjectEaten orig, Player self, IPlayerEdible edible)
        {
            orig(self, edible);

            // Get how much slup likes or dislikes the food
            SlugNPCAI ai = self.AI;
            SlugNPCAI.Food foodType = ai.GetFoodType(edible as PhysicalObject);
            float foodWeight = (foodType != SlugNPCAI.Food.NotCounted) ? ((foodType.Index == -1) ? 0f : Mathf.Abs(ai.foodPreference[foodType.Index])) : 0f;

            // Decide if the slugpup explodes >:3
            if (Random.value < foodWeight * SLUP_EXPLODE_CHANCE)
            {
                // Adapted from Player.Die for slugpups in inv campaign (basically spawns a singularity bomb that instantly explodes)
                AbstractPhysicalObject abstractPhysicalObject = new AbstractPhysicalObject(
                    self.abstractCreature.Room.world,
                    MoreSlugcatsEnums.AbstractObjectType.SingularityBomb,
                    null,
                    self.room.GetWorldCoordinate(self.mainBodyChunk.pos),
                    self.abstractCreature.Room.world.game.GetNewID());
                self.abstractCreature.Room.AddEntity(abstractPhysicalObject);
                abstractPhysicalObject.RealizeInRoom();
                (abstractPhysicalObject.realizedObject as SingularityBomb).Explode();
            }
        }

        private void SlupsSpawnInPipes_Hook(On.Player.orig_SpitOutOfShortCut orig, Player self, RWCustom.IntVector2 pos, Room newRoom, bool spitOutAllSticks)
        {
            orig(self, pos, newRoom, spitOutAllSticks);

            if (!self.isNPC || (Options?.SlupsSpawnSlups.Value ?? false))
            {
                // Create slugpup and spit out of shortcut with player
                AbstractCreature abstractCreature = new(newRoom.world, StaticWorld.GetCreatureTemplate(MoreSlugcatsEnums.CreatureTemplateType.SlugNPC), null, self.abstractCreature.pos, newRoom.game.GetNewID());
                PlayerNPCState state = (abstractCreature.state as PlayerNPCState);
                state.foodInStomach = 1;
                newRoom.abstractRoom.AddEntity(abstractCreature);
                abstractCreature.RealizeInRoom();

                // Make the slugpup like the player (so it will follow)
                SocialMemory.Relationship rel = state.socialMemory.GetOrInitiateRelationship(self.abstractCreature.ID);
                rel.InfluenceLike(1f);
                rel.InfluenceTempLike(1f);
                SlugNPCAbstractAI abstractAI = (abstractCreature.abstractAI as SlugNPCAbstractAI);
                abstractAI.isTamed = true;
                abstractAI.RealAI.friendTracker.friend = self;
                abstractAI.RealAI.friendTracker.friendRel = rel;

                // Create shockwave
                newRoom.AddObject(new ShockWave(new Vector2(self.mainBodyChunk.pos.x, self.mainBodyChunk.pos.y), 300f, 0.2f, 15, false));
            }
        }

        #endregion

        #region iterator stuff

        private void OracleBehavior_Update(On.OracleBehavior.orig_Update orig, OracleBehavior self, bool eu)
        {
            // Five Pebbles closes the game when he realizes you exist
            orig(self, eu);
            if (self.oracle?.ID == Oracle.OracleID.SS && self.player != null && self.player.room == self.oracle.room)
            {
                Application.Quit();
            }
        }

        #endregion

        #region electric death stuff

        private void AddMinorElectricDeath_Hook(On.Room.orig_Loaded orig, Room self)
        {
            orig(self);

            // Check if exists
            bool hasED = false;
            foreach (UpdatableAndDeletable obj in self.updateList)
            {
                if (obj is ElectricDeath)
                {
                    hasED = true;
                    (obj as ElectricDeath).effect.amount = 1f;
                    break;
                }
            }

            if (!hasED && !self.abstractRoom.shelter)
            {
                RoomSettings.RoomEffect effect = new(RoomSettings.RoomEffect.Type.ElectricDeath, 1f, false);
                self.AddObject(new ElectricDeath(effect, self));
            }
        }
        
        private float ElectricDeath_Intensity_get_Hook(Func<ElectricDeath, float> orig, ElectricDeath self)
        {
            return Math.Max((Options?.ElectricHint.Value ?? true) ? MINOR_ELEC_DEATH_AMOUNT : 0f, orig(self));
        }

        #endregion

        #region creatures singularity on death

        private void Creature_BlackHoleOnDeath(On.Creature.orig_Die orig, Creature self) {
            bool wasAlive = self.State.alive;
            
            orig(self);
			
            if (Options.ExplodeOnDeath.Value && wasAlive && self is not Fly) // batflies are exempt so you can actually eat them
            {
			    AbstractPhysicalObject abstractPhysicalObject = new(self.abstractCreature.Room.world, MoreSlugcatsEnums.AbstractObjectType.SingularityBomb, null, self.room.GetWorldCoordinate(self.mainBodyChunk.pos), self.abstractCreature.Room.world.game.GetNewID());
                self.abstractCreature.Room.AddEntity(abstractPhysicalObject);
                abstractPhysicalObject.RealizeInRoom();
			    (abstractPhysicalObject.realizedObject as SingularityBomb).Explode();
            }
        }

        #endregion

        #region neuron flies kill

        private void NeuronFliesKill_Hook(On.PhysicalObject.orig_Collide orig, PhysicalObject self, PhysicalObject otherObject, int myChunk, int otherChunk)
        {
            orig(self, otherObject, myChunk, otherChunk);
            if (self is OracleSwarmer && self is not NSHSwarmer && otherObject is Player && (otherObject as Player).State.alive && !(otherObject as Player).isNPC)
            {
                (otherObject as Player).Die();
            }
        }

        #endregion

        #region absurdly long creatures

        private void LongCentipedes_Hook(ILContext il)
        {
            ILCursor c = new(il);
            c.GotoNext(x => x.MatchNewarr<BodyChunk>());

            // Emit this
            c.Emit(OpCodes.Ldarg_0);
            Instruction newBr = c.Prev;

            // The code that extends the length
            c.EmitDelegate<Func<Centipede, int>>((c) => {
                int i = 0;
                Random.State state = Random.state;
                Random.InitState(c.abstractCreature.ID.RandomSeed);
                while (Random.value < CENTIPEDE_EXTEND_CHANCE)
                {
                    i++;
                }
                Random.state = state;
                return i;
            });
            c.Emit(OpCodes.Add);

            // Fix old break statements so they go to our extend code rather than to the newarr instruction
            c.GotoPrev(MoveType.Before, x => x.Match(OpCodes.Br_S));
            c.Next.Operand = newBr;
            c.GotoPrev(MoveType.Before, x => x.Match(OpCodes.Br_S));
            c.Next.Operand = newBr;
        }

        private void LongLizards_Hook1(ILContext il)
        {
            ILCursor c = new(il);

            // Find the code that creates the body chunks array
            c.GotoNext(MoveType.After, x => x.MatchLdcI4(3));

            // Randomly extend the length
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<Lizard, int>>(l => {
                int i = 0;
                Random.State state = Random.state;
                Random.InitState(l.abstractCreature.ID.RandomSeed);
                while (Random.value < LIZARD_EXTEND_CHANCE)
                {
                    i++;
                }
                Random.state = state;
                return i;
            });
            c.Emit(OpCodes.Add);

            // Replace some of the things
            for (int i = 0; i < 3; i++)
            {
                c.GotoNext(MoveType.Before, x => x.MatchLdcR4(3f));
                c.Remove();
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Func<Lizard, float>>(l => l.bodyChunks.Length);
            }

            // Add the other body chunks
            c.GotoNext(MoveType.Before, x => x.MatchLdcI4(3), x => x.MatchNewarr<PhysicalObject.BodyChunkConnection>());
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Action<Lizard>>(l =>
            {
                for (int i = 3; i < l.bodyChunks.Length; i++)
                {
                    l.bodyChunks[i] = new BodyChunk(l, i, new Vector2(200f, 500f), 8f * l.lizardParams.bodySizeFac * l.lizardParams.bodyRadFac, l.lizardParams.bodyMass / l.bodyChunks.Length);
                }
            });

            // Okay body chunk connections time
            c.Remove();
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<Lizard, int>>(l => l.bodyChunks.Length * 2 - 3);

            // Add the other chunks
            c.GotoNext(MoveType.Before, x => x.MatchLdarg(0), x => x.MatchLdsfld<Lizard.Animation>("Standard"));
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Action<Lizard>>(l =>
            {
                int j = 3;
                for (int i = 3; i < l.bodyChunks.Length; i++, j += 2)
                {
                    l.bodyChunkConnections[j] = new PhysicalObject.BodyChunkConnection(l.bodyChunks[i - 1], l.bodyChunks[i], 17f * l.lizardParams.bodyLengthFac * ((l.lizardParams.bodySizeFac + 1f) / 2f), PhysicalObject.BodyChunkConnection.Type.Normal, 0.95f, 0.5f);
                    l.bodyChunkConnections[j + 1] = new PhysicalObject.BodyChunkConnection(l.bodyChunks[i - 2], l.bodyChunks[i], 17f * l.lizardParams.bodyLengthFac * ((l.lizardParams.bodySizeFac + 1f) / 2f) * (1f + l.lizardParams.bodyStiffnes), PhysicalObject.BodyChunkConnection.Type.Push, 1f - Mathf.Lerp(0.9f, 0.5f, l.lizardParams.bodyStiffnes), 0.5f);
                }
            });
        }

        private void LongLizards_Hook2(ILContext il)
        {
            // Fix some issues related to hardcoding lizard chunks n stuff
            ILCursor c = new(il);
            ILLabel brFalse, brTrue;

            // Something about swimming
            try
            {
                c.GotoNext(x => x.MatchCall<LizardAI>("get_lizard"), x => x.MatchCallvirt<PhysicalObject>("get_bodyChunks"), x => x.MatchLdcI4(2));
                c.GotoPrev(MoveType.Before, x => x.Match(OpCodes.Bgt_S));
                brTrue = c.Next.Operand as ILLabel;
                c.GotoNext(MoveType.After, x => x.Match(OpCodes.Ble_Un_S));
                brFalse = c.Prev.Operand as ILLabel;
                c.Prev.Operand = brTrue;

                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Func<LizardAI, bool>>(l =>
                {
                    for (int i = 3; i < l.lizard.bodyChunks.Length; i++)
                    {
                        if (l.lizard.bodyChunks[1].vel.magnitude > 2f)
                        {
                            return true;
                        }
                    }
                    return false;
                });
                c.Emit(OpCodes.Brfalse, brFalse);
            }
            catch (Exception ex)
            {
                Logger.LogDebug("Swim thingy failed");
                Logger.LogDebug(ex);
                return;
            }

            // Something about being on the ground
            try
            {
                for (int i = 0; i < 3; i++) c.GotoNext(x => x.MatchCallvirt<PhysicalObject>("IsTileSolid"));
                c.GotoNext(MoveType.After, x => x.Match(OpCodes.Brfalse_S));
                brFalse = c.Prev.Operand as ILLabel;

                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Func<LizardAI, bool>>(l =>
                {
                    for (int i = 3; i < l.lizard.bodyChunks.Length; i++)
                    {
                        if (l.lizard.IsTileSolid(i, 0, -1))
                        {
                            return true;
                        }
                    }
                    return false;
                });
                c.Emit(OpCodes.Brfalse, brFalse);

                c.GotoNext(MoveType.Before, x => x.Match(OpCodes.Blt_S));
                c.Index--;
                c.Remove();
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Func<LizardAI, int>>(l => l.lizard.bodyChunks.Length);
            }
            catch (Exception ex)
            {
                Logger.LogDebug("Ground thingy failed");
                Logger.LogDebug(ex);
                return;
            }

        }

        private void LongLizards_Hook3(ILContext il)
        {
            ILCursor c = new(il);

            c.GotoNext(MoveType.Before, x => x.MatchLdcI4(3), x => x.Match(OpCodes.Blt));
            c.Remove();
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<LizardGraphics, int>>(l => l.lizard.bodyChunks.Length);

            /*for (int i = 0; i < 4; i++)
            {
                c.GotoPrev(MoveType.Before, x => x.MatchLdcR4(3f));
                c.Remove();
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Func<LizardGraphics, float>>(l => l.lizard.bodyChunks.Length);
            }*/
        }

        private void LongLizards_Hook4(ILContext il)
        {
            ILCursor c = new(il);
            c.GotoNext(MoveType.After, x => x.MatchCallvirt<PhysicalObject>("get_bodyChunks"));
            c.Remove();
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<LizardGraphics, int>>(l => l.lizard.bodyChunks.Length - 1);
        }

        #endregion

        //
    }
}
