////////////////////////////////////////////////////////////////////////////////////////
// This file is part of the U3 SDK: https://github.com/smartlydressedgames/u3-sdk/    //
// Please refer to the included LICENSE.txt for copyright notice and license details. //
////////////////////////////////////////////////////////////////////////////////////////
using SDG.NetPak;
using System.Collections.Generic;
using UnityEngine;

namespace SDG.Unturned
{
	/// <summary>
	/// One 100 Hz movement command, the equivalent of a Counter-Strike: Source usercmd.
	/// The owning client samples one per tick, predicts it immediately, and batches them into
	/// the regular 12.5 Hz input packet so the server replays exactly the same ticks.
	/// </summary>
	internal struct SourceUserCmd
	{
		public const byte IN_FORWARD = 1 << 0;
		public const byte IN_BACK = 1 << 1;
		public const byte IN_MOVELEFT = 1 << 2;
		public const byte IN_MOVERIGHT = 1 << 3;
		public const byte IN_JUMP = 1 << 4;
		public const byte IN_DUCK = 1 << 5;
		/// <summary>
		/// CS:S walk (+speed). Bound to Unturned's sprint key.
		/// </summary>
		public const byte IN_SPEED = 1 << 6;
		public const int BUTTON_BIT_COUNT = 7;

		public ushort packedYaw;
		public ushort packedPitch;
		public byte buttons;

		public float Yaw => packedYaw * (360.0f / 65536.0f);
		public float Pitch => packedPitch * (180.0f / 65535.0f);

		public bool IsHeld(byte button) => (buttons & button) != 0;

		public int MoveX => (IsHeld(IN_MOVERIGHT) ? 1 : 0) - (IsHeld(IN_MOVELEFT) ? 1 : 0);
		public int MoveZ => (IsHeld(IN_FORWARD) ? 1 : 0) - (IsHeld(IN_BACK) ? 1 : 0);

		public static ushort PackYaw(float yaw)
		{
			yaw %= 360.0f;
			if (yaw < 0.0f)
			{
				yaw += 360.0f;
			}

			return (ushort) (Mathf.RoundToInt(yaw * (65536.0f / 360.0f)) & 0xFFFF);
		}

		public static ushort PackPitch(float pitch)
		{
			return (ushort) Mathf.RoundToInt(Mathf.Clamp(pitch, 0.0f, 180.0f) * (65535.0f / 180.0f));
		}

		public void Write(NetPakWriter writer)
		{
			writer.WriteUInt16(packedYaw);
			writer.WriteUInt16(packedPitch);
			writer.WriteBits(buttons, BUTTON_BIT_COUNT);
		}

		public bool Read(NetPakReader reader)
		{
			bool success = reader.ReadUInt16(out packedYaw);
			success &= reader.ReadUInt16(out packedPitch);
			success &= reader.ReadBits(BUTTON_BIT_COUNT, out uint buttonBits);
			buttons = (byte) buttonBits;
			return success;
		}
	}

	/// <summary>
	/// Everything the CS:S movement needs besides position, velocity and stance to resimulate
	/// identically after a server correction.
	/// </summary>
	internal struct SourceMovementState
	{
		public bool onGround;
		public bool ducked;
		public bool ducking;
		public bool oldJumpHeld;
		public bool oldDuckHeld;
		public bool onLadder;
		public float duckTimeMs;
		public float staminaMs;
		public float surfaceFriction;
		public float groundSurfaceFriction;
		public float groundMaxSpeedMultiplier;
		public Vector3 ladderNormal;

		public void Write(NetPakWriter writer)
		{
			writer.WriteBit(onGround);
			writer.WriteBit(ducked);
			writer.WriteBit(ducking);
			writer.WriteBit(oldJumpHeld);
			writer.WriteBit(oldDuckHeld);
			writer.WriteBit(onLadder);
			writer.WriteFloat(duckTimeMs);
			writer.WriteFloat(staminaMs);
			writer.WriteFloat(surfaceFriction);
			writer.WriteFloat(groundSurfaceFriction);
			writer.WriteFloat(groundMaxSpeedMultiplier);
			writer.WriteFloat(ladderNormal.x);
			writer.WriteFloat(ladderNormal.y);
			writer.WriteFloat(ladderNormal.z);
		}

		public bool Read(NetPakReader reader)
		{
			bool success = reader.ReadBit(out onGround);
			success &= reader.ReadBit(out ducked);
			success &= reader.ReadBit(out ducking);
			success &= reader.ReadBit(out oldJumpHeld);
			success &= reader.ReadBit(out oldDuckHeld);
			success &= reader.ReadBit(out onLadder);
			success &= reader.ReadFloat(out duckTimeMs);
			success &= reader.ReadFloat(out staminaMs);
			success &= reader.ReadFloat(out surfaceFriction);
			success &= reader.ReadFloat(out groundSurfaceFriction);
			success &= reader.ReadFloat(out groundMaxSpeedMultiplier);
			success &= reader.ReadFloat(out ladderNormal.x);
			success &= reader.ReadFloat(out ladderNormal.y);
			success &= reader.ReadFloat(out ladderNormal.z);
			return success;
		}
	}

	/// <summary>
	/// Port of Counter-Strike: Source player movement (CGameMovement / CCSGameMovement) running at 100 ticks per second.
	/// Uses an axis-aligned box hull swept with Unity physics queries instead of the CharacterController capsule.
	/// All CS:S values are written in Source units and converted with METERS_PER_UNIT.
	/// </summary>
	internal class SourceMovement
	{
		/// <summary>
		/// Unturned's 2 m tall character equals the 72 unit tall CS:S player model.
		/// </summary>
		public const float METERS_PER_UNIT = 2.0f / 72.0f;

		public const float TICK_RATE = 100.0f;
		public const float TICK_INTERVAL = 1.0f / TICK_RATE;

		// Server variables in Source units, matching CS:S bhop servers running shavit's bhoptimer (used by
		// SourceJump) with its default "Normal" style. These are not replicated: the server and every client must
		// use the same values or every tick will mispredict.
		public static float sv_gravity = 800.0f;
		public static float sv_friction = 4.0f;
		public static float sv_stopspeed = 75.0f;
		public static float sv_accelerate = 5.0f;
		/// <summary>
		/// bhoptimer Normal style. (Vanilla CS:S is 10.)
		/// </summary>
		public static float sv_airaccelerate = 1000.0f;
		public static float sv_maxspeed = 320.0f;
		public static float sv_maxvelocity = 3500.0f;
		public static float sv_stepsize = 18.0f;
		public static float sv_bounce = 0.0f;

		/// <summary>
		/// bhoptimer Normal style runspeed. (Vanilla CS:S knife is 250.)
		/// Unturned clothing, item and plugin speed multipliers scale this.
		/// </summary>
		public static float runSpeed = 260.0f;

		/// <summary>
		/// Holding jump re-jumps on the first tick back on the ground, so no ground friction is applied.
		/// Same as bhoptimer's autobhop: a held jump counts as a fresh press when landing.
		/// </summary>
		public static bool autoBhop = true;

		/// <summary>
		/// bhoptimer "easybhop": disables CS:S jump fatigue (lower consecutive jumps and post-landing slowdown).
		/// </summary>
		public static bool easyBhop = true;

		/// <summary>
		/// Momentum Mod sv_slope_fix: with autobhop and jump held, hitting an incline that would only cost horizontal
		/// speed acts like flat ground, and landing on a slope keeps the collision only if it gains horizontal speed.
		/// </summary>
		public static bool sv_slope_fix = true;

		/// <summary>
		/// Momentum Mod sv_ramp_fix: retrace from slightly off a surface instead of losing all speed when a trace
		/// comes back stuck (ramp bugs).
		/// </summary>
		public static bool sv_ramp_fix = true;

		/// <summary>
		/// Momentum Mod sv_ramp_bumpcount (4 to 16). Vanilla Source uses 4.
		/// </summary>
		public static int sv_ramp_bumpcount = 8;

		/// <summary>
		/// Momentum Mod sv_ramp_initial_retrace_length, in Source units (0.2 to 5).
		/// </summary>
		public static float sv_ramp_initial_retrace_length = 0.2f;

		public const float CL_FORWARDSPEED = 400.0f;
		public const float CL_SIDESPEED = 400.0f;
		public const float AIR_SPEED_CAP = 30.0f;
		/// <summary>
		/// sqrt(2 * 800 * 57)
		/// </summary>
		public const float JUMP_IMPULSE = 301.993377f;
		public const float NON_JUMP_VELOCITY = 140.0f;
		public const float MIN_WALKABLE_NORMAL = 0.7f;
		public const float GROUND_CHECK_OFFSET = 2.0f;
		public const float COORD_RESOLUTION = 1.0f / 32.0f;
		public const float DUCK_SPEED_MODIFIER = 0.34f;
		public const float WALK_SPEED_MODIFIER = 0.52f;
		/// <summary>
		/// Unturned only: CS:S has no prone. Keeps Unturned's prone-to-crouch speed ratio.
		/// </summary>
		public const float PRONE_SPEED_MODIFIER = 0.2f;
		public const float TIME_TO_DUCK = 0.4f;
		public const float TIME_TO_UNDUCK = 0.2f;
		public const float DUCK_TIME_MS = 1000.0f;
		public const float STAMINA_MAX = 100.0f;
		public const float STAMINA_COST_JUMP = 25.0f;
		public const float STAMINA_RECOVER_RATE = 19.0f;
		/// <summary>
		/// Source MAX_CLIMB_SPEED.
		/// </summary>
		public const float MAX_CLIMB_SPEED = 200.0f;
		/// <summary>
		/// Source LadderDistance(): how far the hull may be from a ladder and still attach to it.
		/// </summary>
		public const float LADDER_DISTANCE = 2.0f;
		/// <summary>
		/// Speed away from the ladder when jumping off.
		/// </summary>
		public const float LADDER_JUMP_SPEED = 270.0f;
		public const float HULL_HALF_WIDTH = 16.0f;
		public const float HULL_HEIGHT_STAND = 62.0f;
		public const float HULL_HEIGHT_DUCK = 45.0f;
		/// <summary>
		/// Footsteps are silent below this horizontal speed, so walking (shift) and crouch-walking are quiet.
		/// </summary>
		public const float FOOTSTEP_MIN_SPEED = 150.0f;
		/// <summary>
		/// Source surface friction used for Unturned materials in the slippery (non-immediately-responsive) mode,
		/// scaled by the material's deceleration multiplier. Roughly Source ice (0.1 * 1.25).
		/// </summary>
		public const float SLIPPERY_SURFACE_FRICTION = 0.125f;

		public static float RunSpeedMeters => U(Mathf.Min(runSpeed, sv_maxspeed));

		/// <summary>
		/// Distance kept between the hull and surfaces after a sweep, in meters. Plays the role of Source's DIST_EPSILON.
		/// </summary>
		private const float SKIN = 0.005f;

		/// <summary>
		/// Physics queries use a box this much smaller (per side, in meters) than the logical hull to stay robust
		/// against touching contacts being reported as overlaps.
		/// </summary>
		private const float SHRINK = 0.0025f;

		/// <summary>
		/// How far behind the start a sweep is retried from when the box starts merely touching something.
		/// </summary>
		private const float TOUCHING_RETRY_BACKOFF = 0.02f;

		/// <summary>
		/// Most a hit is backed off along the movement to keep SKIN of clearance along the surface normal.
		/// (Caps grazing approaches, where SKIN along the normal would mean backing off a long way.)
		/// </summary>
		private const float MAX_HIT_BACKOFF = 0.05f;

		/// <summary>
		/// The "would we be stuck here" test uses a slightly smaller box than sweeps so a hull resting within contact
		/// tolerance of a surface isn't reported as stuck when the sweep said the path was clear.
		/// </summary>
		private const float STUCK_TEST_EXTRA_SHRINK = 0.002f;

		private const int MAX_BUMPS = 4;
		private const int MAX_CLIP_PLANES = 5;

		private static readonly int MOVEMENT_MASK = RayMasks.BLOCK_COLLISION | RayMasks.AGENT | RayMasks.TRAP;

		private static readonly RaycastHit[] castHits = new RaycastHit[32];
		private static readonly Collider[] overlapColliders = new Collider[16];
		private static readonly Vector3[] clipPlanes = new Vector3[MAX_CLIP_PLANES];
		private static readonly Vector3[] stuckOffsets = BuildStuckOffsets();

		private static float U(float units) => units * METERS_PER_UNIT;

		private struct SourceTrace
		{
			public float fraction;
			public Vector3 endpos;
			public Vector3 normal;
			public bool allsolid;
			public bool hit;
			/// <summary>
			/// What was hit. Set even when the hit was found by overlap verification rather than the sweep itself,
			/// in which case raycastHit is default.
			/// </summary>
			public Collider collider;
			public RaycastHit raycastHit;
		}

		private readonly PlayerMovement movement;
		private readonly Player player;
		private readonly Transform transform;

		internal bool onGround;
		/// <summary>
		/// Source m_bDucked / FL_DUCKING: using the duck hull.
		/// </summary>
		internal bool ducked;
		/// <summary>
		/// Source m_bDucking: in a duck or unduck transition.
		/// </summary>
		internal bool ducking;
		internal float duckTimeMs;
		internal bool oldJumpHeld;
		internal bool oldDuckHeld;
		/// <summary>
		/// CS:S m_flStamina: milliseconds of post-jump slowdown remaining.
		/// </summary>
		internal float staminaMs;
		internal float surfaceFriction = 1.0f;
		internal float groundSurfaceFriction = 1.0f;
		internal float groundMaxSpeedMultiplier = 1.0f;
		internal RaycastHit groundHit;

		/// <summary>
		/// Source MOVETYPE_LADDER.
		/// </summary>
		internal bool onLadder;

		/// <summary>
		/// Source m_vecLadderNormal: horizontal direction from the ladder toward the player.
		/// </summary>
		internal Vector3 ladderNormal;

		/// <summary>
		/// Inputs to the most recent tick's Accelerate/AirAccelerate, for the strafe trainer HUD.
		/// Valid only if that tick accelerated toward a wish direction.
		/// </summary>
		internal bool strafeSampleValid;
		internal Vector3 strafeSampleVelocity;
		internal Vector3 strafeSampleWishdir;
		/// <summary>
		/// Speed the acceleration can add up to along wishdir (wishspeed on ground, air speed cap in air).
		/// </summary>
		internal float strafeSampleSpeedCap;
		/// <summary>
		/// Most speed the acceleration can add this tick.
		/// </summary>
		internal float strafeSampleAccelSpeed;

		/// <summary>
		/// Vertical origin change from in-air duck/unduck during the most recent tick.
		/// The local player uses it to keep the camera and interpolation steady.
		/// </summary>
		internal float tickOriginShift;

		private Collider groundSurfaceCollider;
		private bool hasExpectedStance;
		private EPlayerStance expectedStance;
		private bool needsCategorize = true;
		private bool ignoreInitialOverlaps;

		// Per-tick move parameters (Source CMoveData).
		private float frametime = TICK_INTERVAL;
		private bool jumpHeldThisTick;
		private float forwardMove;
		private float sideMove;
		private float maxSpeed;
		private Vector3 forward;
		private Vector3 right;
		/// <summary>
		/// Forward including pitch. Source m_vecForward. (WalkMove/AirMove flatten it, LadderMove does not.)
		/// </summary>
		private Vector3 viewForward;

		public SourceMovement(PlayerMovement movement)
		{
			this.movement = movement;
			player = movement.player;
			transform = movement.transform;
		}

		private Vector3 HullMins => new Vector3(-U(HULL_HALF_WIDTH), 0.0f, -U(HULL_HALF_WIDTH));
		private Vector3 HullMaxs => new Vector3(U(HULL_HALF_WIDTH), HullHeight, U(HULL_HALF_WIDTH));
		private Vector3 StandingHullMaxs => new Vector3(U(HULL_HALF_WIDTH), U(HULL_HEIGHT_STAND), U(HULL_HALF_WIDTH));

		private float HullHeight
		{
			get
			{
				if (player.stance.stance == EPlayerStance.PRONE)
				{
					return PlayerMovement.HEIGHT_PRONE;
				}

				return ducked ? U(HULL_HEIGHT_DUCK) : U(HULL_HEIGHT_STAND);
			}
		}

		/// <summary>
		/// Unturned rule (kept from the original movement): item gravity multipliers (umbrellas, UseableCloud) and
		/// plugin gravity multipliers only apply while falling, so they slow the descent without making jumps higher.
		/// </summary>
		private float Gravity => U(sv_gravity) * (movement.velocity.y <= 0.0f ? movement.totalGravityMultiplier : 1.0f);

		/// <summary>
		/// Unturned rule (kept from the original movement): with reduced gravity, fall speed is capped, which is what
		/// makes umbrellas float down. The cap is in Unturned's own units, so umbrellas float exactly as before.
		/// </summary>
		private void ApplyFallSpeedLimit()
		{
			float multiplier = movement.totalGravityMultiplier;
			if (multiplier >= 0.99f)
				return;

			float minVerticalVelocity = Physics.gravity.y * 2.0f * multiplier;
			if (movement.velocity.y < minVerticalVelocity)
			{
				movement.velocity.y = minVerticalVelocity;
			}
		}

		internal SourceMovementState GetState()
		{
			SourceMovementState state;
			state.onGround = onGround;
			state.ducked = ducked;
			state.ducking = ducking;
			state.oldJumpHeld = oldJumpHeld;
			state.oldDuckHeld = oldDuckHeld;
			state.onLadder = onLadder;
			state.ladderNormal = ladderNormal;
			state.duckTimeMs = duckTimeMs;
			state.staminaMs = staminaMs;
			state.surfaceFriction = surfaceFriction;
			state.groundSurfaceFriction = groundSurfaceFriction;
			state.groundMaxSpeedMultiplier = groundMaxSpeedMultiplier;
			return state;
		}

		internal void SetState(in SourceMovementState state)
		{
			onGround = state.onGround;
			ducked = state.ducked;
			ducking = state.ducking;
			oldJumpHeld = state.oldJumpHeld;
			oldDuckHeld = state.oldDuckHeld;
			onLadder = state.onLadder;
			ladderNormal = state.ladderNormal;
			duckTimeMs = state.duckTimeMs;
			staminaMs = state.staminaMs;
			surfaceFriction = state.surfaceFriction;
			groundSurfaceFriction = state.groundSurfaceFriction;
			groundMaxSpeedMultiplier = state.groundMaxSpeedMultiplier;
			groundHit = default;
			groundSurfaceCollider = null;
			ignoreInitialOverlaps = false;
			needsCategorize = false;

			// Stance was restored alongside this state, so they already agree.
			hasExpectedStance = true;
			expectedStance = player.stance.stance;
		}

		/// <summary>
		/// Called after teleporting or respawning.
		/// </summary>
		internal void OnTeleported()
		{
			needsCategorize = true;
			staminaMs = 0.0f;
			ignoreInitialOverlaps = false;
			onLadder = false;
		}

		/// <summary>
		/// Called after a ladder or swimming tick ran the legacy Unturned code instead.
		/// </summary>
		internal void OnLegacyTick(in SourceUserCmd cmd)
		{
			needsCategorize = true;
			ducked = false;
			ducking = false;
			duckTimeMs = 0.0f;
			onLadder = false;
			// Don't immediately jump when leaving the water or a ladder while holding jump.
			oldJumpHeld = cmd.IsHeld(SourceUserCmd.IN_JUMP);
			oldDuckHeld = cmd.IsHeld(SourceUserCmd.IN_DUCK);
		}

		/// <summary>
		/// Refresh cached friction of the surface underfoot. Called at the 12.5 Hz tick with the material
		/// from PlayerMovement.checkGround because terrain material varies within a single collider.
		/// </summary>
		internal void RefreshGroundMaterial(string materialName)
		{
			if (onGround)
			{
				ApplyGroundMaterial(materialName);
			}
		}

		/// <summary>
		/// CGameMovement::PlayerMove for one 100 Hz tick.
		/// </summary>
		public void Tick(in SourceUserCmd cmd)
		{
			tickOriginShift = 0.0f;
			strafeSampleValid = false;
			frametime = TICK_INTERVAL;
			jumpHeldThisTick = cmd.IsHeld(SourceUserCmd.IN_JUMP);

			SyncDuckStateWithStance();
			// Source only skips this at the start of PlayerMove as an optimization for MOVETYPE_WALK.
			if (needsCategorize || onLadder)
			{
				needsCategorize = false;
				CategorizePosition();
			}

			bool wasOnGround = onGround;
			float fallVelocity = wasOnGround ? 0.0f : -movement.velocity.y;

			ReduceTimers();
			CheckParameters(cmd);
			ResolveStuck();

			bool isProne = player.stance.stance == EPlayerStance.PRONE;
			if (isProne)
			{
				ducked = false;
				ducking = false;
				oldDuckHeld = cmd.IsHeld(SourceUserCmd.IN_DUCK);
			}
			else
			{
				Duck(cmd);
			}

			// If not on a ladder now, but was on one before, get off of the ladder.
			if ((isProne || !LadderMove(cmd)) && onLadder)
			{
				onLadder = false;
			}

			ApplyPendingLaunchVelocity();

			if (onLadder)
			{
				FullLadderMove(cmd);
			}
			else
			{
				FullWalkMove(cmd);
			}

			if (!wasOnGround && onGround)
			{
				movement.NotifySourceLanded(-fallVelocity);
			}
		}

		private void ReduceTimers()
		{
			float frameMs = frametime * 1000.0f;

			if (duckTimeMs > 0.0f)
			{
				duckTimeMs = Mathf.Max(0.0f, duckTimeMs - frameMs);
			}

			if (staminaMs > 0.0f)
			{
				staminaMs = Mathf.Max(0.0f, staminaMs - frameMs);
			}
		}

		private void CheckParameters(in SourceUserCmd cmd)
		{
			Quaternion yawRotation = Quaternion.Euler(0.0f, cmd.Yaw, 0.0f);
			forward = yawRotation * Vector3.forward;
			right = yawRotation * Vector3.right;
			// Unturned pitch is 0 (up) to 180 (down) with 90 level.
			viewForward = Quaternion.Euler(cmd.Pitch - 90.0f, cmd.Yaw, 0.0f) * Vector3.forward;

			forwardMove = cmd.MoveZ * U(CL_FORWARDSPEED);
			sideMove = cmd.MoveX * U(CL_SIDESPEED);

			maxSpeed = RunSpeedMeters * movement.totalSpeedMultiplier;
			if (onGround)
			{
				maxSpeed *= groundMaxSpeedMultiplier;
			}

			if (cmd.IsHeld(SourceUserCmd.IN_SPEED))
			{
				maxSpeed *= WALK_SPEED_MODIFIER;
			}

			if (player.stance.stance == EPlayerStance.PRONE)
			{
				maxSpeed *= PRONE_SPEED_MODIFIER;
			}

			float spd = (forwardMove * forwardMove) + (sideMove * sideMove);
			if (spd > 0.0f && spd > maxSpeed * maxSpeed)
			{
				float ratio = maxSpeed / Mathf.Sqrt(spd);
				forwardMove *= ratio;
				sideMove *= ratio;
			}
		}

		private void ApplyPendingLaunchVelocity()
		{
			Vector3 launch = movement.pendingLaunchVelocity;
			if (launch.sqrMagnitude <= 0.0f)
				return;

			movement.velocity += launch;
			movement.pendingLaunchVelocity = Vector3.zero;

			if (movement.velocity.y > U(NON_JUMP_VELOCITY))
			{
				SetGround(false, default);
			}
		}

		private void FullWalkMove(in SourceUserCmd cmd)
		{
			StartGravity();

			if (cmd.IsHeld(SourceUserCmd.IN_JUMP))
			{
				CheckJumpButton();
			}
			else
			{
				oldJumpHeld = false;
			}

			if (onGround)
			{
				movement.velocity.y = 0.0f;
				Friction();
			}

			CheckVelocity();

			if (onGround)
			{
				WalkMove();
			}
			else
			{
				AirMove();
			}

			CategorizePosition();
			CheckVelocity();
			FinishGravity();

			if (onGround)
			{
				movement.velocity.y = 0.0f;
			}
		}

		private void StartGravity()
		{
			// Yes, this 0.5 looks wrong, but it's not. (Half before the move, half after.)
			movement.velocity.y -= Gravity * 0.5f * frametime;
			ApplyFallSpeedLimit();
			CheckVelocity();
		}

		private void FinishGravity()
		{
			movement.velocity.y -= Gravity * 0.5f * frametime;
			ApplyFallSpeedLimit();
			CheckVelocity();
		}

		private void CheckVelocity()
		{
			Vector3 velocity = movement.velocity;
			if (float.IsNaN(velocity.x) || float.IsNaN(velocity.y) || float.IsNaN(velocity.z))
			{
				movement.velocity = Vector3.zero;
				return;
			}

			float max = U(sv_maxvelocity);
			velocity.x = Mathf.Clamp(velocity.x, -max, max);
			velocity.y = Mathf.Clamp(velocity.y, -max, max);
			velocity.z = Mathf.Clamp(velocity.z, -max, max);
			movement.velocity = velocity;
		}

		private bool CanJump()
		{
			return !player.life.isBroken
				&& player.stance.stance != EPlayerStance.PRONE
				&& !MathfEx.IsNearlyZero(movement.pluginJumpMultiplier, 0.001f);
		}

		private void CheckJumpButton()
		{
			// In the air, so no effect.
			if (!onGround)
			{
				oldJumpHeld = true;
				return;
			}

			// Don't pogo stick. (Autobhop treats a held jump as a fresh press on landing.)
			if (oldJumpHeld && !autoBhop)
				return;

			// Cannot jump while in the unduck transition.
			if (ducking && ducked)
				return;

			if (!CanJump())
			{
				oldJumpHeld = true;
				return;
			}

			SetGround(false, default);

			float jumpSpeed = U(JUMP_IMPULSE) * movement.pluginJumpMultiplier;
			if (ducking || ducked)
			{
				movement.velocity.y = jumpSpeed;
			}
			else
			{
				movement.velocity.y += jumpSpeed;
			}

			// CS:S jump fatigue: consecutive jumps are lower. Disabled by easybhop.
			if (!easyBhop)
			{
				if (staminaMs > 0.0f)
				{
					float ratio = (STAMINA_MAX - ((staminaMs / 1000.0f) * STAMINA_RECOVER_RATE)) / STAMINA_MAX;
					movement.velocity.y *= ratio;
				}

				staminaMs = (STAMINA_COST_JUMP / STAMINA_RECOVER_RATE) * 1000.0f;
			}

			FinishGravity();

			oldJumpHeld = true;
		}

		private void Friction()
		{
			Vector3 velocity = movement.velocity;
			float speed = velocity.magnitude;
			if (speed < U(0.1f))
				return;

			float drop = 0.0f;
			if (onGround)
			{
				float friction = sv_friction * surfaceFriction;
				float stopSpeed = U(sv_stopspeed);
				float control = speed < stopSpeed ? stopSpeed : speed;
				drop += control * friction * frametime;
			}

			float newSpeed = speed - drop;
			if (newSpeed < 0.0f)
			{
				newSpeed = 0.0f;
			}

			if (newSpeed != speed)
			{
				movement.velocity = velocity * (newSpeed / speed);
			}
		}

		private void RecordStrafeSample(Vector3 wishdir, float speedCap, float accelSpeed)
		{
			if (wishdir.sqrMagnitude <= 0.0f)
				return;

			strafeSampleValid = true;
			strafeSampleVelocity = new Vector3(movement.velocity.x, 0.0f, movement.velocity.z);
			strafeSampleWishdir = wishdir;
			strafeSampleSpeedCap = speedCap;
			strafeSampleAccelSpeed = accelSpeed;
		}

		private void Accelerate(Vector3 wishdir, float wishspeed, float accel)
		{
			RecordStrafeSample(wishdir, wishspeed, accel * frametime * wishspeed * surfaceFriction);

			float currentSpeed = Vector3.Dot(movement.velocity, wishdir);
			float addSpeed = wishspeed - currentSpeed;
			if (addSpeed <= 0.0f)
				return;

			float accelSpeed = accel * frametime * wishspeed * surfaceFriction;
			if (accelSpeed > addSpeed)
			{
				accelSpeed = addSpeed;
			}

			movement.velocity += accelSpeed * wishdir;
		}

		private void AirAccelerate(Vector3 wishdir, float wishspeed, float accel)
		{
			float wishspd = wishspeed;
			float airSpeedCap = U(AIR_SPEED_CAP);
			if (wishspd > airSpeedCap)
			{
				wishspd = airSpeedCap;
			}

			RecordStrafeSample(wishdir, wishspd, accel * wishspeed * frametime * surfaceFriction);

			float currentSpeed = Vector3.Dot(movement.velocity, wishdir);
			float addSpeed = wishspd - currentSpeed;
			if (addSpeed <= 0.0f)
				return;

			float accelSpeed = accel * wishspeed * frametime * surfaceFriction;
			if (accelSpeed > addSpeed)
			{
				accelSpeed = addSpeed;
			}

			movement.velocity += accelSpeed * wishdir;
		}

		private void GetWish(out Vector3 wishdir, out float wishspeed)
		{
			Vector3 wishvel = (forward * forwardMove) + (right * sideMove);
			wishvel.y = 0.0f;

			wishspeed = wishvel.magnitude;
			wishdir = wishspeed > 0.0f ? wishvel / wishspeed : Vector3.zero;

			if (wishspeed != 0.0f && wishspeed > maxSpeed)
			{
				wishspeed = maxSpeed;
			}
		}

		private void WalkMove()
		{
			// CS:S post-jump slowdown. Scaled so the stopping power matches GoldSrc at its reference framerate.
			// Never active with easybhop because staminaMs stays zero.
			if (staminaMs > 0.0f)
			{
				float ratio = (STAMINA_MAX - ((staminaMs / 1000.0f) * STAMINA_RECOVER_RATE)) / STAMINA_MAX;
				const float referenceFrametime = 1.0f / 70.0f;
				ratio = Mathf.Pow(ratio, frametime / referenceFrametime);
				movement.velocity.x *= ratio;
				movement.velocity.z *= ratio;
			}

			GetWish(out Vector3 wishdir, out float wishspeed);

			movement.velocity.y = 0.0f;
			Accelerate(wishdir, wishspeed, sv_accelerate);
			movement.velocity.y = 0.0f;

			if (movement.velocity.magnitude < U(1.0f))
			{
				movement.velocity = Vector3.zero;
				return;
			}

			// First try just moving to the destination.
			Vector3 origin = transform.position;
			Vector3 dest = origin + new Vector3(movement.velocity.x * frametime, 0.0f, movement.velocity.z * frametime);
			SourceTrace trace = TracePlayerHull(origin, dest);
			if (trace.fraction == 1.0f)
			{
				SetOrigin(trace.endpos);
				StayOnGround();
				return;
			}

			StepMove();
			StayOnGround();
		}

		private void AirMove()
		{
			GetWish(out Vector3 wishdir, out float wishspeed);
			AirAccelerate(wishdir, wishspeed, sv_airaccelerate);
			TryPlayerMove();
		}

		/// <summary>
		/// CGameMovement::ClipVelocity plus Momentum Mod's sv_slope_fix: with autobhop and jump held, colliding with
		/// a standable incline that would only cost horizontal speed acts as if the surface were flat, so bhopping up
		/// a hill doesn't feel like catching a bump.
		/// </summary>
		private void ClipVelocity(Vector3 inVelocity, Vector3 normal, out Vector3 outVelocity, float overbounce)
		{
			float backoff = Vector3.Dot(inVelocity, normal) * overbounce;
			outVelocity = inVelocity - (normal * backoff);

			// Iterate once to make sure we aren't still moving through the plane.
			float adjust = Vector3.Dot(outVelocity, normal);
			if (adjust < 0.0f)
			{
				outVelocity -= normal * adjust;
			}

			if (sv_slope_fix && autoBhop && jumpHeldThisTick)
			{
				bool canJump = normal.y >= MIN_WALKABLE_NORMAL && outVelocity.y <= U(NON_JUMP_VELOCITY);
				bool isMovingIntoIncline = (normal.x * inVelocity.x) + (normal.z * inVelocity.z) < 0.0f;
				if (canJump && isMovingIntoIncline && HorizontalSqrMagnitude(outVelocity) <= HorizontalSqrMagnitude(inVelocity))
				{
					outVelocity = new Vector3(inVelocity.x, 0.0f, inVelocity.z);
				}
			}
		}

		private static float HorizontalSqrMagnitude(Vector3 vector) => (vector.x * vector.x) + (vector.z * vector.z);

		/// <summary>
		/// Momentum Mod's IsValidMovementTrace.
		/// </summary>
		private bool IsValidMovementTrace(in SourceTrace trace, Vector3 traceStart)
		{
			if (trace.allsolid)
				return false;

			if (trace.fraction <= 0.0f)
				return false;

			if (trace.hit && trace.normal.sqrMagnitude < 1e-6f)
				return false;

			// Would we be stuck at the end position?
			return !IsHullStuckAt(trace.endpos, traceStart);
		}

		/// <summary>
		/// CGameMovement::TryPlayerMove with Momentum Mod's sv_ramp_fix: when a trace comes back stuck, retrace from
		/// slightly off the surface instead of zeroing velocity, and handle the "two identical planes" rampbug.
		/// </summary>
		private void TryPlayerMove()
		{
			int numBumps = sv_ramp_fix ? sv_ramp_bumpcount : MAX_BUMPS;
			int numPlanes = 0;
			Vector3 originalVelocity = movement.velocity;
			Vector3 primalVelocity = movement.velocity;
			Vector3 newVelocity = Vector3.zero;
			Vector3 fixedOrigin = transform.position;
			Vector3 validPlane = Vector3.zero;
			bool hasValidPlane = false;
			bool stuckOnRamp = false;
			SourceTrace trace = default;
			Vector3 end = fixedOrigin;
			float allFraction = 0.0f;
			float timeLeft = frametime;
			float retraceLength = U(sv_ramp_initial_retrace_length);
			string speedLossReason = null;
			SourceTrace firstBlockingTrace = default;
			bool hasFirstBlockingTrace = false;

			for (int bumpCount = 0; bumpCount < numBumps; ++bumpCount)
			{
				if (movement.velocity.sqrMagnitude == 0.0f)
					break;

				if (stuckOnRamp && sv_ramp_fix)
				{
					if (!hasValidPlane)
					{
						if (trace.normal.sqrMagnitude > 1e-6f && validPlane != trace.normal)
						{
							validPlane = trace.normal;
							hasValidPlane = true;
						}
						else
						{
							for (int i = numPlanes - 1; i >= 0; --i)
							{
								if (clipPlanes[i].sqrMagnitude > 1e-6f && validPlane != clipPlanes[i])
								{
									validPlane = clipPlanes[i];
									hasValidPlane = true;
									break;
								}
							}
						}
					}

					if (hasValidPlane)
					{
						float overbounce = validPlane.y >= MIN_WALKABLE_NORMAL ? 1.0f : 1.0f + (sv_bounce * (1.0f - surfaceFriction));
						ClipVelocity(movement.velocity, validPlane, out Vector3 clippedVelocity, overbounce);
						movement.velocity = clippedVelocity;
						originalVelocity = clippedVelocity;
					}
					else
					{
						// We were actually going to be stuck, so search around with a larger box for a valid plane.
						float offsetLength = bumpCount * 2 * retraceLength;
						int validPlaneCount = 0;
						validPlane = Vector3.zero;

						for (int i = 0; i < 3; ++i)
						{
							for (int j = 0; j < 3; ++j)
							{
								for (int h = 0; h < 3; ++h)
								{
									Vector3 offset = new Vector3((i - 1) * offsetLength, (j - 1) * offsetLength, (h - 1) * offsetLength);
									Vector3 offsetMins = offset * 0.5f;
									Vector3 offsetMaxs = offset * 0.5f;
									if (offset.x > 0.0f) offsetMins.x *= 0.5f;
									if (offset.y > 0.0f) offsetMins.y *= 0.5f;
									if (offset.z > 0.0f) offsetMins.z *= 0.5f;
									if (offset.x < 0.0f) offsetMaxs.x *= 0.5f;
									if (offset.y < 0.0f) offsetMaxs.y *= 0.5f;
									if (offset.z < 0.0f) offsetMaxs.z *= 0.5f;

									SourceTrace searchTrace = TraceBox(fixedOrigin + offset, end - offset, HullMins - offsetMins, HullMaxs + offsetMaxs);
									if (searchTrace.hit && !searchTrace.allsolid && searchTrace.fraction > 0.0f && searchTrace.fraction < 1.0f)
									{
										++validPlaneCount;
										validPlane += searchTrace.normal;
									}
								}
							}
						}

						if (validPlaneCount > 0 && validPlane.sqrMagnitude > 1e-6f)
						{
							hasValidPlane = true;
							validPlane.Normalize();
							continue;
						}
					}

					if (hasValidPlane)
					{
						fixedOrigin += validPlane * retraceLength;
					}
					else
					{
						stuckOnRamp = false;
						continue;
					}
				}

				// See if we can make it from origin to end point.
				end = fixedOrigin + (movement.velocity * timeLeft);
				Vector3 traceStart;
				if (stuckOnRamp && hasValidPlane && sv_ramp_fix)
				{
					traceStart = fixedOrigin;
					trace = TracePlayerHull(traceStart, end);
					trace.normal = validPlane;
				}
				else
				{
					traceStart = transform.position;
					trace = TracePlayerHull(traceStart, end);
				}

				if (!hasFirstBlockingTrace && trace.hit && trace.fraction < 1.0f)
				{
					firstBlockingTrace = trace;
					hasFirstBlockingTrace = true;
				}

				if (bumpCount > 0 && sv_ramp_fix && !onGround && !IsValidMovementTrace(trace, traceStart))
				{
					hasValidPlane = false;
					stuckOnRamp = true;
					continue;
				}

				if (trace.allsolid && !sv_ramp_fix)
				{
					// Trapped in another solid.
					movement.velocity = Vector3.zero;
					LogSpeedLoss(primalVelocity, "started inside a solid", trace, trace);
					return;
				}

				if (trace.fraction > 0.0f)
				{
					if ((bumpCount == 0 || onGround || !sv_ramp_fix) && trace.fraction == 1.0f)
					{
						// Precision issue with terrain: a swept box can succeed while ending stuck in a triangle.
						if (IsHullStuckAt(trace.endpos, traceStart))
						{
							if (bumpCount == 0 && sv_ramp_fix)
							{
								hasValidPlane = false;
								stuckOnRamp = true;
								continue;
							}

							movement.velocity = Vector3.zero;
							speedLossReason = "end position would be stuck";
							break;
						}
					}

					if (sv_ramp_fix)
					{
						hasValidPlane = false;
						stuckOnRamp = false;
					}

					// Actually covered some distance.
					originalVelocity = movement.velocity;
					SetOrigin(trace.endpos);
					fixedOrigin = trace.endpos;
					allFraction += trace.fraction;
					numPlanes = 0;
				}

				// If we covered the entire distance, we are done.
				if (trace.fraction == 1.0f)
					break;

				timeLeft -= timeLeft * trace.fraction;

				// Did we run out of planes to clip against?
				if (numPlanes >= MAX_CLIP_PLANES)
				{
					movement.velocity = Vector3.zero;
					speedLossReason = "too many clip planes";
					break;
				}

				clipPlanes[numPlanes] = trace.normal;
				++numPlanes;

				if (numPlanes == 1 && !onGround && !onLadder)
				{
					if (clipPlanes[0].y >= MIN_WALKABLE_NORMAL)
					{
						// Floor or standable slope.
						ClipVelocity(originalVelocity, clipPlanes[0], out newVelocity, 1.0f);
					}
					else
					{
						// Surfing or slammed into a wall.
						ClipVelocity(originalVelocity, clipPlanes[0], out newVelocity, 1.0f + (sv_bounce * (1.0f - surfaceFriction)));
					}

					movement.velocity = newVelocity;
					originalVelocity = newVelocity;
				}
				else
				{
					int i;
					for (i = 0; i < numPlanes; ++i)
					{
						ClipVelocity(originalVelocity, clipPlanes[i], out Vector3 clipped, 1.0f);
						movement.velocity = clipped;

						int j;
						for (j = 0; j < numPlanes; ++j)
						{
							if (j != i && Vector3.Dot(movement.velocity, clipPlanes[j]) < 0.0f)
								break;
						}

						if (j == numPlanes)
							break;
					}

					if (i == numPlanes)
					{
						// Go along the crease.
						if (numPlanes != 2)
						{
							movement.velocity = Vector3.zero;
							speedLossReason = $"wedged between {numPlanes} planes";
							break;
						}

						// Momentum: these lines fix (vertical) rampbug. Clipping into a ramp can make it look like two
						// identical planes, so push away from the ramp horizontally instead of losing speed.
						if (clipPlanes[0] == clipPlanes[1])
						{
							newVelocity = originalVelocity + (clipPlanes[0] * U(20.0f));
							movement.velocity = new Vector3(newVelocity.x, movement.velocity.y, newVelocity.z);
							break;
						}

						Vector3 dir = Vector3.Cross(clipPlanes[0], clipPlanes[1]).normalized;
						float d = Vector3.Dot(dir, movement.velocity);
						movement.velocity = dir * d;
					}

					// If the new velocity is against the original velocity, stop dead to avoid tiny oscillations in corners.
					if (Vector3.Dot(movement.velocity, primalVelocity) <= 0.0f)
					{
						movement.velocity = Vector3.zero;
						speedLossReason = "clipped against original direction";
						break;
					}
				}
			}

			if (allFraction == 0.0f)
			{
				movement.velocity = Vector3.zero;
				speedLossReason ??= "could not move at all";
			}

			LogSpeedLoss(primalVelocity, speedLossReason ?? "collision", firstBlockingTrace, trace);
		}

		/// <summary>
		/// Editor/development builds only: explain sudden speed loss so collision problems can be tracked down.
		/// </summary>
		[System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
		private void LogSpeedLoss(Vector3 velocityBefore, string reason, in SourceTrace firstHitTrace, in SourceTrace lastTrace)
		{
			float speedBefore = Mathf.Sqrt(HorizontalSqrMagnitude(velocityBefore)) / METERS_PER_UNIT;
			float speedAfter = Mathf.Sqrt(HorizontalSqrMagnitude(movement.velocity)) / METERS_PER_UNIT;
			if (speedBefore < 300.0f || speedAfter > speedBefore * 0.5f)
				return;

			if (!movement.channel.IsLocalPlayer || player.input.isResimulating)
				return;

			UnturnedLog.warn($"[SourceMovement] Lost {speedBefore - speedAfter:0} u/s in one tick ({speedBefore:0} -> {speedAfter:0}): {reason}. Position {transform.position} ground {onGround} ladder {onLadder} velocity before {velocityBefore / METERS_PER_UNIT} first hit [{DescribeTrace(firstHitTrace)}] last trace [{DescribeTrace(lastTrace)}] overlapping [{DescribeOverlapsAt(transform.position)}] at last trace end [{DescribeOverlapsAt(lastTrace.endpos)}] unstick [{lastStuckResolution}]");
		}

		private static string DescribeTrace(in SourceTrace trace)
		{
			Collider hitCollider = trace.collider;
			string hitName = hitCollider != null ? $"{hitCollider.name} ({hitCollider.GetType().Name}, layer {LayerMask.LayerToName(hitCollider.gameObject.layer)})" : "nothing";
			return $"{hitName} normal {trace.normal} fraction {trace.fraction:0.###} allsolid {trace.allsolid}";
		}

		private void StepMove()
		{
			Vector3 startPosition = transform.position;
			Vector3 startVelocity = movement.velocity;
			float stepHeight = U(sv_stepsize) + SKIN;

			// Slide move down.
			TryPlayerMove();
			Vector3 downPosition = transform.position;
			Vector3 downVelocity = movement.velocity;

			// Reset.
			SetOrigin(startPosition);
			movement.velocity = startVelocity;

			// Move up a stair height.
			SourceTrace trace = TracePlayerHull(startPosition, startPosition + (Vector3.up * stepHeight));
			if (!trace.allsolid)
			{
				SetOrigin(trace.endpos);
			}

			// Slide move up.
			TryPlayerMove();

			// Move down a stair (attempt to).
			Vector3 upOrigin = transform.position;
			trace = TracePlayerHull(upOrigin, upOrigin - (Vector3.up * stepHeight));

			// If we are not on the ground any more then use the original movement attempt.
			if (!IsWalkable(trace))
			{
				SetOrigin(downPosition);
				movement.velocity = downVelocity;
				return;
			}

			if (!trace.allsolid)
			{
				SetOrigin(trace.endpos);
			}

			Vector3 upPosition = transform.position;

			// Decide which one went farther.
			float downDistSqr = Sqr(downPosition.x - startPosition.x) + Sqr(downPosition.z - startPosition.z);
			float upDistSqr = Sqr(upPosition.x - startPosition.x) + Sqr(upPosition.z - startPosition.z);
			if (downDistSqr > upDistSqr)
			{
				SetOrigin(downPosition);
				movement.velocity = downVelocity;
			}
			else
			{
				// Copy z value from slide move.
				movement.velocity.y = downVelocity.y;
			}
		}

		private void StayOnGround()
		{
			Vector3 origin = transform.position;

			// See how far up we can go without getting stuck.
			SourceTrace trace = TracePlayerHull(origin, origin + (Vector3.up * U(2.0f)));
			Vector3 start = trace.endpos;

			// Now trace down from a known safe position.
			trace = TracePlayerHull(start, origin - (Vector3.up * U(sv_stepsize)));
			if (trace.fraction > 0.0f && trace.fraction < 1.0f && !trace.allsolid && IsWalkable(trace))
			{
				float delta = Mathf.Abs(origin.y - trace.endpos.y);
				if (delta > 0.5f * U(COORD_RESOLUTION))
				{
					SetOrigin(trace.endpos);
				}
			}
		}

		private void CategorizePosition()
		{
			// Reset this each time we re-categorize.
			surfaceFriction = 1.0f;

			// Shooting up really fast. Definitely not on ground.
			// On ladder moving up, so not on ground either.
			if (movement.velocity.y > U(NON_JUMP_VELOCITY) || (movement.velocity.y > 0.0f && onLadder))
			{
				SetGround(false, default);
				return;
			}

			Vector3 origin = transform.position;
			Vector3 point = origin - (Vector3.up * U(GROUND_CHECK_OFFSET));
			SourceTrace trace = TracePlayerHull(origin, point);
			if (!IsWalkable(trace))
			{
				// Test four sub-boxes, to see if any of them would have found shallower slope we could actually stand on.
				// The sub-boxes are inside the full hull, so they can't hit anything the full hull missed.
				if (trace.hit && !trace.allsolid)
				{
					trace = TryTouchGroundInQuadrants(origin, point, trace);
				}
				if (!IsWalkable(trace))
				{
					SetGround(false, trace);

					// Sliding up a steep slope (surf ramp).
					if (movement.velocity.y > 0.0f)
					{
						surfaceFriction = 0.25f;
					}

					return;
				}
			}

			// Momentum sv_slope_fix: only update velocity as if we collided with the ground if it results in
			// horizontal speed gain (landing on a downhill slope). Otherwise landing just zeroes vertical speed.
			if (sv_slope_fix)
			{
				ClipVelocity(movement.velocity, trace.normal, out Vector3 nextVelocity, 1.0f);
				if (HorizontalSqrMagnitude(nextVelocity) > HorizontalSqrMagnitude(movement.velocity))
				{
					movement.velocity = nextVelocity;
				}
			}

			SetGround(true, trace);
		}

		private SourceTrace TryTouchGroundInQuadrants(Vector3 start, Vector3 end, SourceTrace fallback)
		{
			Vector3 minsSrc = HullMins;
			Vector3 maxsSrc = HullMaxs;
			SourceTrace trace;

			// -x -z
			trace = TraceBox(start, end, minsSrc, new Vector3(Mathf.Min(0.0f, maxsSrc.x), maxsSrc.y, Mathf.Min(0.0f, maxsSrc.z)));
			if (IsWalkable(trace))
				return trace;

			// +x +z
			trace = TraceBox(start, end, new Vector3(Mathf.Max(0.0f, minsSrc.x), minsSrc.y, Mathf.Max(0.0f, minsSrc.z)), maxsSrc);
			if (IsWalkable(trace))
				return trace;

			// -x +z
			trace = TraceBox(start, end, new Vector3(minsSrc.x, minsSrc.y, Mathf.Max(0.0f, minsSrc.z)), new Vector3(Mathf.Min(0.0f, maxsSrc.x), maxsSrc.y, maxsSrc.z));
			if (IsWalkable(trace))
				return trace;

			// +x -z
			trace = TraceBox(start, end, new Vector3(Mathf.Max(0.0f, minsSrc.x), minsSrc.y, minsSrc.z), new Vector3(maxsSrc.x, maxsSrc.y, Mathf.Min(0.0f, maxsSrc.z)));
			if (IsWalkable(trace))
				return trace;

			return fallback;
		}

		private void SetGround(bool grounded, in SourceTrace trace)
		{
			if (grounded)
			{
				onGround = true;
				groundHit = trace.raycastHit;
				CategorizeGroundSurface(trace.collider);
				surfaceFriction = groundSurfaceFriction;
				movement.velocity.y = 0.0f;
			}
			else
			{
				onGround = false;
				groundHit = default;
			}
		}

		private void CategorizeGroundSurface(Collider collider)
		{
			if (collider == groundSurfaceCollider)
				return;

			groundSurfaceCollider = collider;

			string materialName;
			if (collider == null)
			{
				materialName = null;
			}
			else if (collider.CompareTag("Ground"))
			{
				materialName = PhysicsTool.GetTerrainMaterialName(transform.position);
			}
			else
			{
				materialName = collider.sharedMaterial != null ? collider.sharedMaterial.name : null;
			}

			ApplyGroundMaterial(materialName);
		}

		private void ApplyGroundMaterial(string materialName)
		{
			PhysicsMaterialCharacterFrictionProperties properties = PhysicMaterialCustomData.GetCharacterFrictionProperties(materialName);
			if (properties.mode == EPhysicsMaterialCharacterFrictionMode.ImmediatelyResponsive)
			{
				groundSurfaceFriction = 1.0f;
				groundMaxSpeedMultiplier = 1.0f;
			}
			else
			{
				groundSurfaceFriction = Mathf.Clamp(SLIPPERY_SURFACE_FRICTION * properties.decelerationMultiplier, 0.01f, 1.0f);
				groundMaxSpeedMultiplier = properties.maxSpeedMultiplier;
			}
		}

		#region Ladders

		/// <summary>
		/// CGameMovement::LadderMove. Returns true while the player is on (or just jumped off) a ladder.
		/// Unturned ladders are colliders tagged "Ladder" whose transform.up faces away from the climbable side.
		/// </summary>
		private bool LadderMove(in SourceUserCmd cmd)
		{
			Vector3 wishdir;
			if (onLadder)
			{
				// If I'm already moving on a ladder, use the previous ladder direction.
				wishdir = -ladderNormal;
			}
			else
			{
				// Otherwise, use the direction player is attempting to move.
				if (forwardMove == 0.0f && sideMove == 0.0f)
					return false;

				wishdir = (viewForward * forwardMove) + (right * sideMove);
				if (wishdir.sqrMagnitude < 1e-8f)
					return false;

				wishdir.Normalize();
			}

			// wishdir points toward the ladder if any exists.
			Vector3 origin = transform.position;
			if (!TraceLadder(origin, wishdir, out Vector3 normal))
				return false;

			onLadder = true;
			ladderNormal = normal;

			// On ladder, convert movement to be relative to the ladder.
			Vector3 floor = origin + (Vector3.down * U(1.0f));
			bool onFloor = onGround || Physics.CheckSphere(floor, 0.001f, MOVEMENT_MASK, QueryTriggerInteraction.Ignore);

			float climbSpeed = U(MAX_CLIMB_SPEED);
			float forwardSpeed = 0.0f;
			float rightSpeed = 0.0f;
			if (cmd.IsHeld(SourceUserCmd.IN_BACK))
			{
				forwardSpeed -= climbSpeed;
			}
			if (cmd.IsHeld(SourceUserCmd.IN_FORWARD))
			{
				forwardSpeed += climbSpeed;
			}
			if (cmd.IsHeld(SourceUserCmd.IN_MOVELEFT))
			{
				rightSpeed -= climbSpeed;
			}
			if (cmd.IsHeld(SourceUserCmd.IN_MOVERIGHT))
			{
				rightSpeed += climbSpeed;
			}

			if (cmd.IsHeld(SourceUserCmd.IN_JUMP))
			{
				// Jump off the ladder.
				onLadder = false;
				movement.velocity = normal * U(LADDER_JUMP_SPEED);
			}
			else if (forwardSpeed != 0.0f || rightSpeed != 0.0f)
			{
				// Calculate player's intended velocity.
				Vector3 velocity = (viewForward * forwardSpeed) + (right * rightSpeed);

				// Perpendicular in the ladder plane.
				Vector3 perp = Vector3.Cross(Vector3.up, normal).normalized;

				// Decompose velocity into ladder plane.
				float normalSpeed = Vector3.Dot(velocity, normal);

				// This is the velocity into the face of the ladder.
				Vector3 cross = normal * normalSpeed;

				// This is the player's additional velocity.
				Vector3 lateral = velocity - cross;

				// This turns the velocity into the face of the ladder into velocity that is roughly vertically
				// perpendicular to the face of the ladder. It IS possible to face up and move down or face down and
				// move up because the velocity is a sum of the directional velocity and the converted velocity through
				// the face of the ladder -- by design.
				Vector3 up = Vector3.Cross(normal, perp);
				movement.velocity = lateral - (normalSpeed * up);

				// On ground moving away from the ladder.
				if (onFloor && normalSpeed > 0.0f)
				{
					movement.velocity += normal * U(MAX_CLIMB_SPEED);
				}
			}
			else
			{
				movement.velocity = Vector3.zero;
			}

			return true;
		}

		/// <summary>
		/// CGameMovement::FullLadderMove.
		/// </summary>
		private void FullLadderMove(in SourceUserCmd cmd)
		{
			if (cmd.IsHeld(SourceUserCmd.IN_JUMP))
			{
				CheckJumpButton();
			}
			else
			{
				oldJumpHeld = false;
			}

			TryPlayerMove();
		}

		/// <summary>
		/// Sweep the hull LadderDistance toward wishdir looking for a ladder. Unturned ladder volumes aren't solid,
		/// so a hull already inside one also counts. Like Unturned's own climbing, only the front/back of upright
		/// ladders can be climbed.
		/// </summary>
		private bool TraceLadder(Vector3 origin, Vector3 direction, out Vector3 normal)
		{
			normal = Vector3.zero;

			Vector3 mins = HullMins;
			Vector3 maxs = HullMaxs;
			Vector3 center = origin + ((maxs + mins) * 0.5f);
			Vector3 halfExtents = ((maxs - mins) * 0.5f) - new Vector3(SHRINK, SHRINK, SHRINK);
			float distance = U(LADDER_DISTANCE) + SKIN;

			int hitCount = Physics.BoxCastNonAlloc(center, halfExtents, direction, castHits, Quaternion.identity, distance, RayMasks.LADDER_INTERACT, QueryTriggerInteraction.Collide);

			Collider ladder = null;
			float closestDistance = float.MaxValue;
			for (int index = 0; index < hitCount; ++index)
			{
				RaycastHit hit = castHits[index];
				Collider collider = hit.collider;
				if (collider == null || IsSelf(collider) || !collider.CompareTag("Ladder"))
					continue;

				Vector3 ladderUp = collider.transform.up;

				// Prevent climbing angled ladders. Only "mostly" up/down ladders.
				if (Mathf.Abs(ladderUp.y) > 0.1f)
					continue;

				bool isInitialOverlap = hit.distance <= 0.0f && hit.point == Vector3.zero;
				if (!isInitialOverlap && Mathf.Abs(Vector3.Dot(hit.normal, ladderUp)) <= 0.9f)
					continue; // Hit the side or top of the ladder rather than the front/back.

				if (hit.distance < closestDistance)
				{
					closestDistance = hit.distance;
					ladder = collider;
				}
			}

			if (ladder == null)
				return false;

			Vector3 facing = ladder.transform.up;
			facing.y = 0.0f;
			facing.Normalize();

			// Face toward whichever side of the ladder the player is on.
			if (Vector3.Dot(center - ladder.bounds.center, facing) < 0.0f)
			{
				facing = -facing;
			}

			normal = facing;
			return true;
		}

		#endregion

		#region Ducking

		/// <summary>
		/// If something other than this class changed the stance (spawn, prone, ladder, swimming, vehicle exit,
		/// server correction, plugins) adopt it as the duck state.
		/// </summary>
		private void SyncDuckStateWithStance()
		{
			EPlayerStance stance = player.stance.stance;
			if (hasExpectedStance && stance == expectedStance)
				return;

			hasExpectedStance = true;
			expectedStance = stance;
			ducking = false;
			duckTimeMs = 0.0f;
			ducked = stance == EPlayerStance.CROUCH;
		}

		private void Duck(in SourceUserCmd cmd)
		{
			bool duckHeld = cmd.IsHeld(SourceUserCmd.IN_DUCK);
			bool duckPressed = duckHeld && !oldDuckHeld;
			bool duckReleased = !duckHeld && oldDuckHeld;
			oldDuckHeld = duckHeld;

			bool inAir = !onGround;
			bool inDuck = ducked;

			// HandleDuckingSpeedCrop: slow down ducked players.
			if (ducked && onGround)
			{
				forwardMove *= DUCK_SPEED_MODIFIER;
				sideMove *= DUCK_SPEED_MODIFIER;
			}

			if (duckHeld || ducking || inDuck)
			{
				if (duckHeld)
				{
					// Have the duck button pressed, but the player currently isn't in the duck position.
					if (duckPressed && !inDuck)
					{
						duckTimeMs = DUCK_TIME_MS;
						ducking = true;
					}

					// The player is in duck transition.
					if (ducking)
					{
						float duckSeconds = Mathf.Max(0.0f, DUCK_TIME_MS - duckTimeMs) * 0.001f;

						// Finish in duck transition when transition time is over, in "duck", in air.
						if (duckSeconds > TIME_TO_DUCK || inDuck || inAir)
						{
							FinishDuck();
						}
					}
				}
				else
				{
					// We released the duck button, so start the unduck transition.
					if (duckReleased)
					{
						if (inDuck)
						{
							duckTimeMs = DUCK_TIME_MS;
						}
						else if (ducking && !ducked)
						{
							// Invert time if released before fully ducked.
							float unduckMs = 1000.0f * TIME_TO_UNDUCK;
							float duckMs = 1000.0f * TIME_TO_DUCK;
							float elapsedMs = DUCK_TIME_MS - duckTimeMs;
							float fracDucked = elapsedMs / duckMs;
							float remainingUnduckMs = fracDucked * unduckMs;
							duckTimeMs = DUCK_TIME_MS - unduckMs + remainingUnduckMs;
						}
					}

					if (CanUnduck())
					{
						if (ducking || ducked)
						{
							float duckSeconds = Mathf.Max(0.0f, DUCK_TIME_MS - duckTimeMs) * 0.001f;

							// Finish ducking immediately if duck time is over or not on ground.
							if (duckSeconds > TIME_TO_UNDUCK || inAir)
							{
								FinishUnDuck();
							}
							else
							{
								ducking = true;
							}
						}
					}
					else if (duckTimeMs != DUCK_TIME_MS)
					{
						// Still under something where we can't unduck, so make sure we reset this timer so
						// that we'll unduck once we exit the tunnel, etc.
						duckTimeMs = DUCK_TIME_MS;
						ducked = true;
						ducking = false;
					}
				}
			}

			UpdateStanceFromDuck(duckHeld);
		}

		private void FinishDuck()
		{
			if (ducked)
				return;

			ducked = true;
			ducking = false;

			if (!onGround)
			{
				// In the air: pull the feet up so the head stays put. (This is what makes crouch jumping higher.)
				float delta = U(HULL_HEIGHT_STAND - HULL_HEIGHT_DUCK);
				SetOrigin(transform.position + (Vector3.up * delta));
				tickOriginShift += delta;
				movement.OnSourceDuckOriginShift(delta);
			}

			CategorizePosition();
		}

		private void FinishUnDuck()
		{
			if (!onGround && ducked)
			{
				// In the air: push the feet back down so the head stays put.
				float delta = -U(HULL_HEIGHT_STAND - HULL_HEIGHT_DUCK);
				SetOrigin(transform.position + (Vector3.up * delta));
				tickOriginShift += delta;
				movement.OnSourceDuckOriginShift(delta);
			}

			ducked = false;
			ducking = false;
			duckTimeMs = 0.0f;

			CategorizePosition();
		}

		private bool CanUnduck()
		{
			if (!ducked)
				return true;

			Vector3 origin = transform.position;
			Vector3 newOrigin = origin;
			if (!onGround)
			{
				// If in air and letting go of crouch, make sure we can offset origin to make up for uncrouching.
				newOrigin.y -= U(HULL_HEIGHT_STAND - HULL_HEIGHT_DUCK);
			}

			SourceTrace trace = TraceBox(origin, newOrigin, HullMins, StandingHullMaxs);
			return !trace.allsolid && trace.fraction == 1.0f;
		}

		/// <summary>
		/// Unturned's stance drives animation, eye height, hitbox and accuracy, so mirror the duck state into it.
		/// The stance switches at the start of a duck/unduck transition so the view moves like the CS:S view offset.
		/// </summary>
		private void UpdateStanceFromDuck(bool duckHeld)
		{
			EPlayerStance current = player.stance.stance;
			if (current == EPlayerStance.STAND || current == EPlayerStance.CROUCH || current == EPlayerStance.SPRINT)
			{
				bool crouchVisual = ducked ? !ducking : (ducking && duckHeld);
				EPlayerStance target = crouchVisual ? EPlayerStance.CROUCH : EPlayerStance.STAND;
				if (target != current)
				{
					player.stance.SetStanceFromSourceMovement(target);
				}
			}

			hasExpectedStance = true;
			expectedStance = player.stance.stance;
		}

		#endregion

		#region Collision

		private void SetOrigin(Vector3 position)
		{
			transform.position = position;
		}

		private static float Sqr(float value) => value * value;

		private bool IsSelf(Collider collider)
		{
			return collider == movement.controller;
		}

		private static bool IsAgent(Collider collider)
		{
			// Unturned rule: zombies and animals are solid, but can't be stood on.
			return collider != null && collider.gameObject.layer == LayerMasks.AGENT;
		}

		private static bool IsWalkable(in SourceTrace trace)
		{
			return trace.hit && !trace.allsolid && trace.normal.y >= MIN_WALKABLE_NORMAL && !IsAgent(trace.collider);
		}

		private SourceTrace TracePlayerHull(Vector3 start, Vector3 end)
		{
			return TraceBox(start, end, HullMins, HullMaxs);
		}

		/// <summary>
		/// Sweep an axis-aligned box (relative to the origin at the feet) from start to end. Equivalent of
		/// Source's TracePlayerBBox: fraction is 1 if nothing was hit, endpos is where the box stopped.
		/// </summary>
		private SourceTrace TraceBox(Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs)
		{
			SourceTrace trace = default;
			trace.fraction = 1.0f;
			trace.endpos = end;

			Vector3 halfExtents = (maxs - mins) * 0.5f;
			Vector3 centerOffset = (maxs + mins) * 0.5f;
			Vector3 castHalfExtents = new Vector3(
				Mathf.Max(halfExtents.x - SHRINK, 0.001f),
				Mathf.Max(halfExtents.y - SHRINK, 0.001f),
				Mathf.Max(halfExtents.z - SHRINK, 0.001f));

			Vector3 delta = end - start;
			float distance = delta.magnitude;
			if (distance < 1e-6f)
			{
				if (!ignoreInitialOverlaps && IsBoxOverlapping(start + centerOffset, castHalfExtents))
				{
					trace.allsolid = true;
					trace.hit = true;
					trace.fraction = 0.0f;
					trace.endpos = start;
				}

				return trace;
			}

			Vector3 direction = delta / distance;

			float closestDistance = float.MaxValue;
			int closestIndex = -1;
			int overlapIndex = -1;
			Vector3 overlapNormal = Vector3.zero;
			// The first attempt sweeps from start. If that starts merely touching something, the second attempt sweeps
			// the same box from slightly behind start so the touched surface comes back as a real contact with a real
			// normal. Distances are always measured from start.
			float castBackoff = 0.0f;
			for (int attempt = 0; attempt < 2; ++attempt)
			{
				Vector3 castCenter = start + centerOffset - (direction * castBackoff);
				float castLength = castBackoff + distance + MAX_HIT_BACKOFF;
				int hitCount = Physics.BoxCastNonAlloc(castCenter, castHalfExtents, direction, castHits, Quaternion.identity, castLength, MOVEMENT_MASK, QueryTriggerInteraction.Ignore);

				closestDistance = float.MaxValue;
				closestIndex = -1;
				overlapIndex = -1;
				bool hasTouchingOverlap = false;
				for (int index = 0; index < hitCount; ++index)
				{
					RaycastHit hit = castHits[index];
					if (hit.collider == null || IsSelf(hit.collider))
						continue;

					// Unity reports colliders overlapping (or merely touching) the box at the start of the sweep with
					// zero distance and point, and a normal that just opposes the sweep. Using that as-is blocks moving in
					// any direction, including away from it, which traps the player.
					if (hit.distance <= 0.0f && hit.point == Vector3.zero)
					{
						if (ignoreInitialOverlaps)
							continue;

						if (!GetSeparation(hit.collider, castCenter, castHalfExtents, out Vector3 separation, out _))
						{
							// Only touching, not penetrating (common skimming terrain and road meshes). There is no real
							// direction to block along. Retry from behind, or if this already is the retry, it is not in
							// the way of this move (touching surfaces we move into show up as hits on the retry).
							hasTouchingOverlap = true;
							continue;
						}

						// Actually inside it: moving away from it is free, moving into it is blocked and slides along
						// the direction out of it.
						if (Vector3.Dot(direction, separation) >= 0.0f)
							continue;

						if (overlapIndex < 0)
						{
							overlapIndex = index;
							overlapNormal = separation;
						}

						continue;
					}

					float distanceFromStart = hit.distance - castBackoff;
					if (distanceFromStart < closestDistance)
					{
						closestDistance = distanceFromStart;
						closestIndex = index;
					}
				}

				if (!hasTouchingOverlap || overlapIndex >= 0 || attempt > 0)
					break;

				castBackoff = TOUCHING_RETRY_BACKOFF;
			}

			if (overlapIndex >= 0)
			{
				trace.hit = true;
				trace.fraction = 0.0f;
				trace.endpos = start;
				trace.normal = overlapNormal;
				trace.raycastHit = castHits[overlapIndex];
				trace.collider = castHits[overlapIndex].collider;
				return trace;
			}

			if (closestIndex < 0)
				return VerifyNoMissedHit(trace, start, direction, distance, mins, maxs);

			// Stop short of the surface by SKIN measured along its normal, not along the movement. Backing off along a
			// shallow approach (e.g. landing on a road at speed) would otherwise leave almost no gap, and the next sweep
			// would start touching it.
			Vector3 normal = GetSurfaceNormal(castHits[closestIndex]);
			float approach = -Vector3.Dot(direction, normal);
			float backoff = Mathf.Min(SKIN / Mathf.Max(approach, SKIN / MAX_HIT_BACKOFF), MAX_HIT_BACKOFF);
			float moveDistance = closestDistance - backoff;
			if (moveDistance >= distance)
				return VerifyNoMissedHit(trace, start, direction, distance, mins, maxs); // Surface is just past the end.

			moveDistance = Mathf.Max(0.0f, moveDistance);
			trace.fraction = moveDistance / distance;
			trace.endpos = start + (direction * moveDistance);
			trace.normal = normal;
			trace.hit = true;
			trace.raycastHit = castHits[closestIndex];
			trace.collider = castHits[closestIndex].collider;
			return VerifyNoMissedHit(trace, start, direction, distance, mins, maxs);
		}

		/// <summary>
		/// Contact resolution of the "missed hit" search: binary search steps along the move, about 1.5 mm at 40 cm.
		/// </summary>
		private const int MISSED_HIT_SEARCH_STEPS = 8;

		private static readonly Collider[] missedHitStartColliders = new Collider[16];

		/// <summary>
		/// Unity's box sweep can pass straight through some non-convex mesh colliders (e.g. PEI's Road_Line/Road_Tee
		/// meshes) while an overlap test at the end position clearly finds them. If the end of the move is newly inside
		/// something, binary search along the move for the contact and report it as a hit, with the surface direction
		/// from the exact penetration query.
		/// </summary>
		private SourceTrace VerifyNoMissedHit(SourceTrace trace, Vector3 start, Vector3 direction, float distance, Vector3 mins, Vector3 maxs)
		{
			if (ignoreInitialOverlaps)
				return trace;

			float traveled = trace.fraction * distance;
			if (traveled <= 0.0f)
				return trace;

			Vector3 centerOffset = (maxs + mins) * 0.5f;
			float shrink = SHRINK + STUCK_TEST_EXTRA_SHRINK;
			Vector3 halfExtents = new Vector3(
				Mathf.Max(((maxs.x - mins.x) * 0.5f) - shrink, 0.001f),
				Mathf.Max(((maxs.y - mins.y) * 0.5f) - shrink, 0.001f),
				Mathf.Max(((maxs.z - mins.z) * 0.5f) - shrink, 0.001f));

			// Usually nothing at the end, so this is the only extra query.
			int endCount = Physics.OverlapBoxNonAlloc(trace.endpos + centerOffset, halfExtents, overlapColliders, Quaternion.identity, MOVEMENT_MASK, QueryTriggerInteraction.Ignore);
			if (endCount == 0)
				return trace;

			int startCount = Physics.OverlapBoxNonAlloc(start + centerOffset, halfExtents, missedHitStartColliders, Quaternion.identity, MOVEMENT_MASK, QueryTriggerInteraction.Ignore);
			Collider missedCollider = FindNewOverlap(endCount, startCount);
			if (missedCollider == null)
				return trace;

			// Largest distance along the move that is still clear of anything new.
			float clear = 0.0f;
			float blocked = traveled;
			for (int step = 0; step < MISSED_HIT_SEARCH_STEPS; ++step)
			{
				float middle = (clear + blocked) * 0.5f;
				int count = Physics.OverlapBoxNonAlloc(start + (direction * middle) + centerOffset, halfExtents, overlapColliders, Quaternion.identity, MOVEMENT_MASK, QueryTriggerInteraction.Ignore);
				Collider newCollider = FindNewOverlap(count, startCount);
				if (newCollider != null)
				{
					blocked = middle;
					missedCollider = newCollider;
				}
				else
				{
					clear = middle;
				}
			}

			Vector3 contactCenter = start + (direction * blocked) + centerOffset;
			if (!GetSeparation(missedCollider, contactCenter, halfExtents, out Vector3 normal, out _))
			{
				normal = -direction;
			}

			// Same clearance rule as a regular hit: SKIN along the surface normal.
			float approach = -Vector3.Dot(direction, normal);
			float backoff = Mathf.Min(SKIN / Mathf.Max(approach, SKIN / MAX_HIT_BACKOFF), MAX_HIT_BACKOFF);
			float moveDistance = Mathf.Max(0.0f, clear - backoff);

			trace.fraction = moveDistance / distance;
			trace.endpos = start + (direction * moveDistance);
			trace.normal = normal;
			trace.hit = true;
			trace.allsolid = false;
			trace.collider = missedCollider;
			trace.raycastHit = default;
			return trace;
		}

		/// <summary>
		/// First collider in overlapColliders[0..count) that isn't self and wasn't in missedHitStartColliders[0..startCount).
		/// </summary>
		private Collider FindNewOverlap(int count, int startCount)
		{
			for (int index = 0; index < count; ++index)
			{
				Collider collider = overlapColliders[index];
				if (collider == null || IsSelf(collider))
					continue;

				bool wasAlreadyOverlapping = false;
				for (int startIndex = 0; startIndex < startCount; ++startIndex)
				{
					if (missedHitStartColliders[startIndex] == collider)
					{
						wasAlreadyOverlapping = true;
						break;
					}
				}

				if (!wasAlreadyOverlapping)
					return collider;
			}

			return null;
		}

		/// <summary>
		/// A box sweep touching a terrain triangle edge reports a blend of the neighboring faces' normals, which
		/// can point partly against the movement and eat speed. Source traces report the plane of the triangle hit,
		/// so re-sample the actual face under the contact point.
		/// </summary>
		private static Vector3 GetSurfaceNormal(in RaycastHit hit)
		{
			Vector3 normal = hit.normal;
			if (hit.collider is TerrainCollider)
			{
				Ray ray = new Ray(hit.point + (normal * 0.05f), -normal);
				if (hit.collider.Raycast(ray, out RaycastHit faceHit, 0.1f) && faceHit.normal.sqrMagnitude > 0.5f)
				{
					return faceHit.normal;
				}
			}

			return normal;
		}

		private bool IsBoxOverlapping(Vector3 center, Vector3 halfExtents)
		{
			int count = Physics.OverlapBoxNonAlloc(center, halfExtents, overlapColliders, Quaternion.identity, MOVEMENT_MASK, QueryTriggerInteraction.Ignore);
			for (int index = 0; index < count; ++index)
			{
				Collider collider = overlapColliders[index];
				if (collider != null && !IsSelf(collider))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Unswept hull test used by TryPlayerMove: would the hull be stuck at origin?
		/// (Never while already stuck and allowed to move out of whatever we are inside.)
		/// </summary>
		private static readonly Collider[] stuckTestStartColliders = new Collider[16];

		/// <summary>
		/// Would the hull moved from traceStart to origin end up inside something new? Colliders the hull already
		/// overlapped at traceStart don't count: sweeps deliberately let the hull move away from or along those, and
		/// ResolveStuck deals with them. (Counting them made every move "stuck" while resting against e.g. a road.)
		/// </summary>
		private bool IsHullStuckAt(Vector3 origin, Vector3 traceStart)
		{
			if (ignoreInitialOverlaps)
				return false;

			GetStuckTestBox(out Vector3 centerOffset, out Vector3 halfExtents);
			int endCount = Physics.OverlapBoxNonAlloc(origin + centerOffset, halfExtents, overlapColliders, Quaternion.identity, MOVEMENT_MASK, QueryTriggerInteraction.Ignore);
			int startCount = -1;
			for (int endIndex = 0; endIndex < endCount; ++endIndex)
			{
				Collider collider = overlapColliders[endIndex];
				if (collider == null || IsSelf(collider))
					continue;

				if (startCount < 0)
				{
					startCount = Physics.OverlapBoxNonAlloc(traceStart + centerOffset, halfExtents, stuckTestStartColliders, Quaternion.identity, MOVEMENT_MASK, QueryTriggerInteraction.Ignore);
				}

				bool wasAlreadyOverlapping = false;
				for (int startIndex = 0; startIndex < startCount; ++startIndex)
				{
					if (stuckTestStartColliders[startIndex] == collider)
					{
						wasAlreadyOverlapping = true;
						break;
					}
				}

				if (!wasAlreadyOverlapping)
					return true;
			}

			return false;
		}

		private void GetStuckTestBox(out Vector3 centerOffset, out Vector3 halfExtents)
		{
			Vector3 mins = HullMins;
			Vector3 maxs = HullMaxs;
			centerOffset = (maxs + mins) * 0.5f;
			float shrink = SHRINK + STUCK_TEST_EXTRA_SHRINK;
			halfExtents = ((maxs - mins) * 0.5f) - new Vector3(shrink, shrink, shrink);
		}

		/// <summary>
		/// Development logging: what the hull overlaps at a position.
		/// </summary>
		private string DescribeOverlapsAt(Vector3 origin)
		{
			GetStuckTestBox(out Vector3 centerOffset, out Vector3 halfExtents);
			int count = Physics.OverlapBoxNonAlloc(origin + centerOffset, halfExtents, overlapColliders, Quaternion.identity, MOVEMENT_MASK, QueryTriggerInteraction.Ignore);
			System.Text.StringBuilder builder = new System.Text.StringBuilder();
			for (int index = 0; index < count; ++index)
			{
				Collider collider = overlapColliders[index];
				if (collider == null || IsSelf(collider))
					continue;

				if (builder.Length > 0)
				{
					builder.Append(", ");
				}

				Vector3 center = origin + centerOffset;
				bool isPenetrating = GetSeparation(collider, center, halfExtents, out Vector3 separation, out float depth);
				builder.Append($"{collider.name} ({collider.GetType().Name}, layer {LayerMask.LayerToName(collider.gameObject.layer)}");
				builder.Append(isPenetrating ? $", out {separation} by {depth:0.####})" : ", touching)");
			}

			return builder.Length > 0 ? builder.ToString() : "nothing";
		}

		private bool IsHullOverlapping(Vector3 origin)
		{
			Vector3 mins = HullMins;
			Vector3 maxs = HullMaxs;
			Vector3 halfExtents = (maxs - mins) * 0.5f;
			Vector3 center = origin + ((maxs + mins) * 0.5f);
			halfExtents.x -= SHRINK;
			halfExtents.y -= SHRINK;
			halfExtents.z -= SHRINK;
			return IsBoxOverlapping(center, halfExtents);
		}

		/// <summary>
		/// Most the hull is pushed out of overlapping geometry in one tick. Deeper overlaps fall back to the nudge
		/// search so a vehicle or door can't shove the player most of the way through a wall and out the other side.
		/// </summary>
		private const float MAX_DEPENETRATION = 0.5f;
		private const int DEPENETRATION_ITERATIONS = 4;
		private const float FALLBACK_DEPENETRATION_STEP = 0.05f;

		private static BoxCollider separationProbe;

		/// <summary>
		/// Shared box collider used only as the shape for Physics.ComputePenetration. It's a trigger on the
		/// ignore-raycast layer (which only collides with unused built-in layers), parked far below the world.
		/// </summary>
		private static BoxCollider GetSeparationProbe()
		{
			if (separationProbe == null)
			{
				GameObject probeObject = new GameObject("SourceMovementHullProbe");
				probeObject.hideFlags = HideFlags.HideAndDontSave;
				probeObject.layer = LayerMasks.IGNORE_RAYCAST;
				probeObject.transform.position = new Vector3(0.0f, -100000.0f, 0.0f);
				Object.DontDestroyOnLoad(probeObject);
				separationProbe = probeObject.AddComponent<BoxCollider>();
				separationProbe.isTrigger = true;
			}

			return separationProbe;
		}

		/// <summary>
		/// Direction (and depth) that separates a box at hullCenter from other. Returns false if they don't actually
		/// penetrate (e.g. only touching), in which case direction and distance are meaningless.
		/// </summary>
		private static bool GetSeparation(Collider other, Vector3 hullCenter, Vector3 halfExtents, out Vector3 direction, out float distance)
		{
			BoxCollider probe = GetSeparationProbe();
			probe.size = halfExtents * 2.0f;

			Transform otherTransform = other.transform;
			return Physics.ComputePenetration(probe, hullCenter, Quaternion.identity, other, otherTransform.position, otherTransform.rotation, out direction, out distance)
				&& direction.sqrMagnitude > 0.5f
				&& distance > 0.0f;
		}

		/// <summary>
		/// Largest collider (bounds extents) for which "away from its center" is a sensible way out.
		/// </summary>
		private const float MAX_FALLBACK_SEPARATION_EXTENTS = 3.0f;

		/// <summary>
		/// Way out when Unity can't compute an exact separation. Away from the center works for small things like
		/// zombies, but for terrain or a large mesh the center can be hundreds of meters away, so go up instead.
		/// </summary>
		private static Vector3 GetFallbackSeparationDirection(Collider other, Vector3 hullCenter)
		{
			if (other is TerrainCollider)
				return Vector3.up;

			Bounds bounds = other.bounds;
			if (bounds.extents.sqrMagnitude > MAX_FALLBACK_SEPARATION_EXTENTS * MAX_FALLBACK_SEPARATION_EXTENTS)
				return Vector3.up;

			Vector3 direction = hullCenter - bounds.center;
			if (direction.sqrMagnitude < 1e-8f)
				return Vector3.up;

			return direction.normalized;
		}

		/// <summary>
		/// Push the hull out of everything it overlaps along the exact separating directions, removing any velocity
		/// into those obstacles. Returns true if the hull ends up free.
		/// </summary>
		/// <summary>
		/// Step up out of whatever is overlapping the bottom of the hull (curb edge, step, terrain), like walking up a
		/// stair: go up at most a step height, then settle back down onto the surface. The shortest way out of a curb
		/// edge is often sideways or backwards, which leaves the player wedged on it instead of on top of it.
		/// </summary>
		private bool TryLiftOut()
		{
			Vector3 origin = transform.position;
			float maxLift = U(sv_stepsize) + SKIN;

			// Room above? (Overlaps we are moving up out of don't block this sweep.)
			SourceTrace upTrace = TracePlayerHull(origin, origin + (Vector3.up * maxLift));
			if (upTrace.fraction <= 0.0f)
				return false;

			// Come back down onto whatever we were stuck in.
			SourceTrace downTrace = TracePlayerHull(upTrace.endpos, origin);
			Vector3 lifted = downTrace.endpos;
			if (lifted.y <= origin.y + 1e-4f)
				return false;

			if (IsHullOverlapping(lifted))
				return false;

			SetOrigin(lifted);

			// Don't keep driving downward into the surface we were just lifted onto.
			if (movement.velocity.y < 0.0f)
			{
				movement.velocity.y = 0.0f;
			}

			return true;
		}

		private bool TryDepenetrate()
		{
			Vector3 startOrigin = transform.position;
			Vector3 origin = startOrigin;
			Vector3 mins = HullMins;
			Vector3 maxs = HullMaxs;
			Vector3 centerOffset = (maxs + mins) * 0.5f;
			Vector3 halfExtents = ((maxs - mins) * 0.5f) - new Vector3(SHRINK, SHRINK, SHRINK);

			for (int iteration = 0; iteration < DEPENETRATION_ITERATIONS; ++iteration)
			{
				Vector3 center = origin + centerOffset;
				int count = Physics.OverlapBoxNonAlloc(center, halfExtents, overlapColliders, Quaternion.identity, MOVEMENT_MASK, QueryTriggerInteraction.Ignore);

				Vector3 push = Vector3.zero;
				for (int index = 0; index < count; ++index)
				{
					Collider collider = overlapColliders[index];
					if (collider == null || IsSelf(collider))
						continue;

					// Exact separation when Unity can compute it, otherwise step away from the collider's center.
					bool isExact = GetSeparation(collider, center, halfExtents, out Vector3 direction, out float depth);
					if (!isExact)
					{
						direction = GetFallbackSeparationDirection(collider, center);
					}

					push += direction * (isExact ? depth + SKIN : FALLBACK_DEPENETRATION_STEP);

					// Stop pushing into it.
					float speedInto = Vector3.Dot(movement.velocity, direction);
					if (speedInto < 0.0f)
					{
						movement.velocity -= direction * speedInto;
					}
				}

				if (push == Vector3.zero)
					break;

				origin += push;
				if ((origin - startOrigin).sqrMagnitude > MAX_DEPENETRATION * MAX_DEPENETRATION)
					return false;
			}

			if (IsHullOverlapping(origin))
				return false;

			SetOrigin(origin);
			return true;
		}

		/// <summary>
		/// Equivalent of CGameMovement::CheckStuck. Something moved into the player (vehicle, door, zombie, barricade)
		/// or the player was pushed into something, so push out along the separating direction. If that fails, try
		/// nudging, and as a last resort allow movement out of whatever we are inside of.
		/// </summary>
		/// <summary>
		/// Development logging: what ResolveStuck did at the start of the most recent tick.
		/// </summary>
		private string lastStuckResolution = "none";

		private void ResolveStuck()
		{
			Vector3 origin = transform.position;
			if (!IsHullOverlapping(origin))
			{
				ignoreInitialOverlaps = false;
				lastStuckResolution = "not overlapping";
				return;
			}

			// Prefer stepping up out of it, then the shortest way out in any direction.
			if (TryLiftOut())
			{
				ignoreInitialOverlaps = false;
				lastStuckResolution = $"lifted {transform.position.y - origin.y:0.####} m";
				return;
			}

			if (TryDepenetrate())
			{
				ignoreInitialOverlaps = false;
				lastStuckResolution = $"pushed out {transform.position - origin}";
				return;
			}

			foreach (Vector3 offset in stuckOffsets)
			{
				if (!IsHullOverlapping(origin + offset))
				{
					SetOrigin(origin + offset);
					ignoreInitialOverlaps = false;
					lastStuckResolution = $"nudged {offset}";
					return;
				}
			}

			ignoreInitialOverlaps = true;
			lastStuckResolution = "gave up, ignoring overlaps";
		}

		private static Vector3[] BuildStuckOffsets()
		{
			List<Vector3> offsets = new List<Vector3>();
			float[] distances = { 0.01f, 0.02f, 0.04f, 0.08f, 0.15f, 0.25f };
			foreach (float distance in distances)
			{
				offsets.Add(Vector3.up * distance);
				for (int index = 0; index < 8; ++index)
				{
					float angle = index * 45.0f * Mathf.Deg2Rad;
					offsets.Add(new Vector3(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle)) * distance);
				}

				if (distance <= 0.04f)
				{
					offsets.Add(Vector3.down * distance);
				}
			}

			return offsets.ToArray();
		}

		#endregion
	}
}
