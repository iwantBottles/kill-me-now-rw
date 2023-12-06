using System;
using BepInEx;
using UnityEngine;
using SlugBase.Features;
using static SlugBase.Features.FeatureTypes;
using MoreSlugcats;
using Random = UnityEngine.Random;
using MonoMod.RuntimeDetour;
using BepInEx.Logging;

namespace SlugTemplate
{
    [BepInPlugin(MOD_ID, "Kill Me Now RW", "0.1.0")]
    class Plugin : BaseUnityPlugin
    {
        private const string MOD_ID = "bottles.killmenowrw";
        private const float SLUP_EXPLODE_CHANCE = 0.1f; // chance to explode when abs(slugpup food pref) = 1
        private const float MINOR_ELEC_DEATH_AMOUNT = 0.02f; // 50% is where it becomes lethal; don't set it to that

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

                // Put your custom hooks here!
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

            if(MeanLizards.TryGet(world.game, out float meanness))
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

            if(!wasDead && self.dead
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
    }
}