////////////////////////////////////////////////////////////////////////////////////////
// This file is part of the U3 SDK: https://github.com/smartlydressedgames/u3-sdk/    //
// Please refer to the included LICENSE.txt for copyright notice and license details. //
////////////////////////////////////////////////////////////////////////////////////////
#if UNITY_EDITOR || DEVELOPMENT_BUILD
// #define WITH_MOVEMENT_GIZMOS
// #define LOG_FOOTSTEP_AUDIO
// #define WITH_FLOOR_SNAPPING_GIZMOS
#endif // UNITY_EDITOR || DEVELOPMENT_BUILD
using SDG.Framework.Devkit;
using SDG.Framework.Water;
using SDG.NetTransport;
using Steamworks;
using System.Collections.Generic;
using UnityEngine;

namespace SDG.Unturned
{
	public delegate void Landed(float velocity);
	public delegate void Seated(bool isDriver, bool inVehicle, bool wasVehicle, InteractableVehicle oldVehicle, InteractableVehicle newVehicle);
	public delegate void VehicleUpdated(bool isDriveable, ushort newFuel, ushort maxFuel, float newSpeed, float minSpeed, float maxSpeed, ushort newHealth, ushort maxHealth, ushort newBatteryCharge);
	public delegate void SafetyUpdated(bool isSafe);
	public delegate void RadiationUpdated(bool isRadiated);
	public delegate void PurchaseUpdated(HordePurchaseVolume newNode);

	public struct PlayerStateUpdate
	{
		public Vector3 pos;
		public byte angle;
		public byte rot;

		public PlayerStateUpdate(Vector3 pos, byte angle, byte rot)
		{
			this.pos = pos;
			this.angle = angle;
			this.rot = rot;
		}
	}

	public enum EPlayerHeight
	{
		STAND,
		CROUCH,
		PRONE
	}

	public class PlayerMovement : PlayerCaller
	{
		public static readonly float HEIGHT_STAND = 2;
		public static readonly float HEIGHT_CROUCH = 1.2f;
		public static readonly float HEIGHT_PRONE = 0.8f;

		/// <summary>
		/// Nelson 2024-10-18: Moved to a constant because clients need this value for footsteps and they don't have the
		/// character controller component.
		/// </summary>
		internal const float SKIN_WIDTH = 0.1f;

		public static bool forceTrustClient
		{
			get => GameplayConfigData._forceTrustClient;
			set
			{
				GameplayConfigData._forceTrustClient.value = value;

				UnturnedLog.info("Set ForceTrustClient to: " + forceTrustClient);
			}
		}

		public Landed onLanded;
		public Seated onSeated;
		public VehicleUpdated onVehicleUpdated;
		public SafetyUpdated onSafetyUpdated;
		public RadiationUpdated onRadiationUpdated;
		public PurchaseUpdated onPurchaseUpdated;

		public PlayerRegionUpdated onRegionUpdated;
		public PlayerBoundUpdated onBoundUpdated;

		public event PlayerNavChanged PlayerNavChanged;
		private void TriggerPlayerNavChanged(byte oldNav, byte newNav)
		{
			if (PlayerNavChanged == null)
			{
				return;
			}

			PlayerNavChanged(this, oldNav, newNav);
		}

		private static readonly float SPEED_CLIMB = 4.5f;
		private static readonly float SPEED_SWIM = 3f;

		// Walking speeds and jumping are CS:S values in SourceMovement.
		private static readonly float SWIM = 3;

		/// <summary>
		/// Note: Only UpdateCharacterControllerEnabled should modify whether controller is enabled.
		/// (turning off and back on is fine though)
		/// </summary>
		public CharacterController controller
		{
			get;
			protected set;
		}

		[System.Obsolete("Was current value of interpolated aiming speed multiplier.")]
		public float _multiplier;
		[System.Obsolete("Was target value of interpolated aiming speed multiplier.")]
		public float multiplier;
		public float itemGravityMultiplier;
		public float pluginGravityMultiplier;
		public float pluginSpeedMultiplier;
		public float pluginJumpMultiplier = 1.0f;

		public float totalGravityMultiplier => itemGravityMultiplier * pluginGravityMultiplier;

		public float totalSpeedMultiplier => pluginSpeedMultiplier * player.clothing.movementSpeedMultiplier
					* (player.equipment.asset?.equipableMovementSpeedMultiplier ?? 1.0f)
					* (player.equipment.useable?.movementSpeedMultiplier ?? 1.0f);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
		public bool enableFly;
#endif // UNITY_EDITOR || DEVELOPMENT_BUILD

		private float lastFootstep;

		[System.Obsolete]
		public SDG.Framework.Landscapes.LandscapeHoleVolume landscapeHoleVolume => null;

		internal bool CanEnterTeleporter => player.life.IsAlive && getVehicle() == null;

		private void DoTeleport(Transform inputTransform, Transform outputTransform)
		{
			Vector3 relativePosition = inputTransform.InverseTransformPoint(transform.position);
			Quaternion relativeRotation = inputTransform.InverseTransformRotation(transform.rotation);
			transform.position = outputTransform.TransformPoint(relativePosition);
			Quaternion outputRotation = outputTransform.TransformRotation(relativeRotation);
			float yaw = outputRotation.eulerAngles.y;
			player.look.TeleportYaw(yaw);

			// Adjusting last update pos is particularly important for first-person view interpolation.
			Vector3 relativeLastUpdatePos = inputTransform.InverseTransformPoint(lastUpdatePos);
			lastUpdatePos = outputTransform.TransformPoint(relativeLastUpdatePos);

			source?.OnTeleported();

			player.PostTeleport();
		}

		internal void EnterCollisionTeleporter(CollisionTeleporter teleporter)
		{
			Transform inputTransform = teleporter.transform;
			Transform outputTransform = teleporter.DestinationTransform;
			DoTeleport(inputTransform, outputTransform);
		}

		internal void EnterTeleporterVolume(TeleporterEntranceVolume entrance, TeleporterExitVolume exit)
		{
			Transform inputTransform = entrance.transform;
			Transform outputTransform = exit.transform;
			DoTeleport(inputTransform, outputTransform);
		}

		internal void UpdateCharacterControllerEnabled()
		{
			if (controller != null)
			{
				controller.enabled = vehicle == null && player.life.IsAlive;
			}
		}

		private bool _isGrounded;
		public bool isGrounded => _isGrounded;

		private bool _isSafe;
		public bool isSafe
		{
			get => _isSafe;

			set => _isSafe = value;
		}

		public SafezoneNode isSafeInfo;

		private bool _isRadiated;
		public bool isRadiated
		{
			get => _isRadiated;

			set => _isRadiated = value;
		}

		/// <summary>
		/// Valid while isRadiated.
		/// </summary>
		public IDeadzoneNode ActiveDeadzone { get; private set; }

		private HordePurchaseVolume _purchaseNode;
		public HordePurchaseVolume purchaseNode
		{
			get => _purchaseNode;

			set => _purchaseNode = value;
		}

		public IAmbianceNode effectNode
		{
			get;
			private set;
		}

		[System.Obsolete]
		public bool inRain;

		public bool inSnow;

		/// <summary>
		/// Set according to volume or level global asset fallback.
		/// </summary>
		public uint WeatherMask
		{
			get;
			protected set;
		}

		private string materialName;

		// Plays swimming audio if true. Placeholder-ish if we want to support custom liquids in the future.
		private bool materialIsWater;

		public RaycastHit ground;

		internal EPlayerHeight height = EPlayerHeight.STAND;
		public void setSize(EPlayerHeight newHeight)
		{
			if (newHeight == height)
			{
				return;
			}
			height = newHeight;

			applySize();
		}

		private void applySize()
		{
			float size;
			switch (height)
			{
				case EPlayerHeight.STAND:
					size = HEIGHT_STAND;
					break;
				case EPlayerHeight.CROUCH:
					size = HEIGHT_CROUCH;
					break;
				case EPlayerHeight.PRONE:
					size = HEIGHT_PRONE;
					break;
				default:
					size = 2.0f;
					break;
			}

			if (channel.IsLocalPlayer || Provider.isServer)
			{
				if (controller != null)
				{
					// Nelson 2024-09-23: If prone on a surface slightly above a death barrier (e.g., on the staircase
					// of the air traffic control tower) and standing up the capsule could hit the trigger and kill the
					// player. (private issue #2551) At first I wondered if we could work around this by changing the
					// order of setting height/center, but sadly that doesn't seem to be the case. Fortunately,
					// disabling the collider and re-enabling after changing the size seems to work.
					bool wasControllerEnabled = controller.enabled;
					controller.enabled = false;
					controller.height = size;
					controller.center = new Vector3(0, size * 0.5f, 0);
					controller.enabled = wasControllerEnabled;
				}
			}
		}

		private bool _isMoving;
		public bool isMoving => _isMoving;

		public float speed
		{
			get
			{
				if (player.stance.stance == EPlayerStance.SWIM)
				{
					return SPEED_SWIM * (1f + (player.skills.mastery((int) EPlayerSpeciality.OFFENSE, (int) EPlayerOffense.DIVING) * 0.25f)) * totalSpeedMultiplier;
				}
				else
				{
					float exercise = 1f + (player.skills.mastery((int) EPlayerSpeciality.OFFENSE, (int) EPlayerOffense.EXERCISE) * 0.25f);

					if (player.stance.stance == EPlayerStance.CLIMB)
					{
						return SPEED_CLIMB * exercise * totalSpeedMultiplier;
					}

					// CS:S max ground speeds (no skill bonus). The actual walking simulation is in SourceMovement.
					float runSpeed = SourceMovement.RunSpeedMeters * totalSpeedMultiplier;
					if (player.stance.stance == EPlayerStance.STAND || player.stance.stance == EPlayerStance.SPRINT)
					{
						return runSpeed;
					}
					else if (player.stance.stance == EPlayerStance.CROUCH)
					{
						return runSpeed * SourceMovement.DUCK_SPEED_MODIFIER;
					}
					else if (player.stance.stance == EPlayerStance.PRONE)
					{
						return runSpeed * SourceMovement.PRONE_SPEED_MODIFIER;
					}
				}

				return 0;
			}
		}

		private Vector3 _move;
		public Vector3 move => _move;

		private byte _region_x;
		public byte region_x => _region_x;

		private byte _region_y;
		public byte region_y => _region_y;

		private byte _bound;
		public byte bound => _bound;

		private byte _nav;
		public byte nav => _nav;

		private byte updateRegionOld_X;
		private byte updateRegionOld_Y;
		private byte updateRegionNew_X;
		private byte updateRegionNew_Y;
		private byte updateRegionIndex;

		private LoadedRegion[,] _loadedRegions;
		public LoadedRegion[,] loadedRegions => _loadedRegions;

		private LoadedBound[] _loadedBounds;
		public LoadedBound[] loadedBounds => _loadedBounds;

		internal Vector3 velocity;
		public float fall => velocity.y;

		public Vector3 pendingLaunchVelocity;

		[System.Obsolete]
		public Vector3 real => transform.position;

		private Vector3 lastUpdatePos;
		public PitchYawSnapshotInfo snapshot;
		private NetworkSnapshotBuffer<PitchYawSnapshotInfo> nsb;

		private byte _horizontal = 1;
		public byte horizontal => _horizontal;

		private byte _vertical = 1;
		public byte vertical => _vertical;

		private int warp_x;
		private int warp_y;
		internal int input_x;
		internal int input_y;

		private bool _jump;
		public bool jump => _jump;

		/// <summary>
		/// Was set to true during teleport, and restored to false during the next movement tick.
		/// 
		/// Server pauses movement when this is set until next client update that matches,
		/// in order to prevent rubberbanding following a teleport.
		/// </summary>
		[System.Obsolete]
		public bool isAllowed;

		[System.Obsolete]
		public bool isUpdated;

		public List<PlayerStateUpdate> updates;
		public bool canAddSimulationResultsToUpdates;
		/// <summary>
		/// Used instead of actual position to avoid revealing admins in "vanish" mode.
		/// </summary>
		internal PlayerStateUpdate mostRecentlyAddedUpdate;
		internal bool hasMostRecentlyAddedUpdate;

		/// <summary>
		/// Flag for plugins to allow maintenance access underneath the map.
		/// </summary>
		public bool bypassUndergroundWhitelist = false;

		internal bool hasPendingVehicleChange;
		internal InteractableVehicle pendingVehicle;
		private byte pendingSeatIndex;
		private Transform pendingSeatTransform;
		private Vector3 pendingSeatPosition;
		private byte pendingSeatAngle;

		private Vector3 lastStatPos;
		private float lastStatTime;

		private InteractableVehicle vehicle;
		private byte seat;

		public InteractableVehicle getVehicle()
		{
			return vehicle;
		}

		/// <summary>
		/// Get seat (if any), otherwise null.
		/// </summary>
		public Passenger getVehicleSeat()
		{
			return vehicle != null && vehicle.passengers != null && seat < vehicle.passengers.Length ? vehicle.passengers[seat] : null;
		}

		public byte getSeat()
		{
			return seat;
		}

		/// <summary>
		/// Get ragdoll effect to use when running enemies over with the current vehicle.
		/// </summary>
		public ERagdollEffect GetVehicleRagdollEffect()
		{
			// Player can temporarily disable ragdoll effects along with their cosmetic/skin effects for stealth.
			if (vehicle != null && player.clothing.isMythic)
			{
				if (channel.owner.GetVehicleSkinItemDefId(vehicle, out int itemdefid))
				{
					if (channel.owner.TryGetRagdollEffectForItemDef(itemdefid, out ERagdollEffect effect))
					{
						return effect;
					}

					SkinAsset skinAsset = Assets.find(EAssetType.SKIN, Provider.provider.economyService.getInventorySkinID(itemdefid)) as SkinAsset;
					if (skinAsset != null)
					{
						return skinAsset.ragdollEffect;
					}
				}
			}

			return ERagdollEffect.None;
		}

		internal void ApplyPendingVehicleChange()
		{
			hasPendingVehicleChange = false;

			InteractableVehicle oldVehicle = vehicle;
			vehicle = pendingVehicle;
			seat = pendingSeatIndex;

			bool isDriver = vehicle != null && seat == 0;

			if (vehicle == null) // remove
			{
				player.transform.parent = pendingSeatTransform;
				player.ReceiveTeleport(pendingSeatPosition, pendingSeatAngle);
			}

			if (channel.IsLocalPlayer)
			{
				if (isDriver && Level.info != null && Level.info.name.ToLower() != "tutorial")
				{
					bool data;
					if (Provider.provider.achievementsService.getAchievement("Wheel", out data) && !data)
					{
						Provider.provider.achievementsService.setAchievement("Wheel");
					}
				}

				if (vehicle != null)
				{
					PlayerUI.disableDot();

					if (player.equipment.useable is UseableGun gun)
					{
						gun.UpdateCrosshairEnabled();
					}
				}
				else
				{
					if (player.equipment.useable is UseableGun gun)
					{
						gun.UpdateCrosshairEnabled();
					}
					else
					{
						PlayerUI.enableDot();
					}
				}
			}

			if (channel.IsLocalPlayer || Provider.isServer)
			{
				UpdateCharacterControllerEnabled();

				if (vehicle != null)
				{
					if (isDriver)
					{
						player.stance.checkStance(EPlayerStance.DRIVING);
					}
					else
					{
						player.stance.checkStance(EPlayerStance.SITTING);
					}
				}
				else
				{
					player.stance.checkStance(EPlayerStance.STAND);
				}
			}

			if (channel.IsLocalPlayer)
			{
				onSeated?.Invoke(isDriver, vehicle != null, oldVehicle != null, oldVehicle, vehicle);

				if (isDriver)
				{
					if (onVehicleUpdated != null)
					{
						ushort displayCurrentFuel;
						ushort displayMaxFuel;
						vehicle.getDisplayFuel(out displayCurrentFuel, out displayMaxFuel);

						onVehicleUpdated(!vehicle.isUnderwater && !vehicle.isDead, displayCurrentFuel, displayMaxFuel, vehicle.AnimatedForwardVelocity, vehicle.asset.TargetReverseVelocity, vehicle.asset.TargetForwardVelocity, vehicle.health, vehicle.asset.health, vehicle.batteryCharge);
					}
				}

				if (vehicle != null)
				{
					if (isDriver)
					{
						if (oldVehicle == null)
						{
							PlayerUI.message(EPlayerMessage.VEHICLE_EXIT, "");
						}
						else
						{
							PlayerUI.message(EPlayerMessage.VEHICLE_SWAP, "");
						}
					}
					else
					{
						PlayerUI.message(EPlayerMessage.VEHICLE_SWAP, "");
					}
				}
			}

			if (vehicle != null)
			{
				player.transform.parent = pendingSeatTransform;
				player.transform.localPosition = pendingSeatPosition;
				player.transform.localRotation = Quaternion.identity;

				player.look.updateLook();
			}
		}

		public void setVehicle(InteractableVehicle newVehicle, byte newSeat, Transform newSeatingTransform, Vector3 newSeatingPosition, byte newSeatingAngle, bool forceUpdate)
		{
			hasPendingVehicleChange = true;
			pendingVehicle = newVehicle;
			pendingSeatIndex = newSeat;
			pendingSeatTransform = newSeatingTransform;
			pendingSeatPosition = newSeatingPosition;
			pendingSeatAngle = newSeatingAngle;

			if (channel.IsLocalPlayer || Provider.isServer)
			{
				if (player.life.IsAlive && !forceUpdate)
				{
					return;
				}
			}

			ApplyPendingVehicleChange();
		}

		[System.Obsolete]
		public void tellPluginGravityMultiplier(CSteamID steamID, float newPluginGravityMultiplier)
		{
			ReceivePluginGravityMultiplier(newPluginGravityMultiplier);
		}

		private static readonly ClientInstanceMethod<float> SendPluginGravityMultiplier = ClientInstanceMethod<float>.Get(typeof(PlayerMovement), nameof(ReceivePluginGravityMultiplier));
		[SteamCall(ESteamCallValidation.ONLY_FROM_SERVER, legacyName = nameof(tellPluginGravityMultiplier))]
		public void ReceivePluginGravityMultiplier(float newPluginGravityMultiplier)
		{
			pluginGravityMultiplier = newPluginGravityMultiplier;
		}

		public void sendPluginGravityMultiplier(float newPluginGravityMultiplier)
		{
			pluginGravityMultiplier = newPluginGravityMultiplier;

			if (!channel.IsLocalPlayer)
			{
				SendPluginGravityMultiplier.Invoke(GetNetId(), ENetReliability.Reliable, channel.GetOwnerTransportConnection(), newPluginGravityMultiplier);
			}
		}

		[System.Obsolete]
		public void tellPluginJumpMultiplier(CSteamID steamID, float newPluginJumpMultiplier)
		{
			ReceivePluginJumpMultiplier(newPluginJumpMultiplier);
		}

		private static readonly ClientInstanceMethod<float> SendPluginJumpMultiplier = ClientInstanceMethod<float>.Get(typeof(PlayerMovement), nameof(ReceivePluginJumpMultiplier));
		[SteamCall(ESteamCallValidation.ONLY_FROM_SERVER, legacyName = nameof(tellPluginJumpMultiplier))]
		public void ReceivePluginJumpMultiplier(float newPluginJumpMultiplier)
		{
			pluginJumpMultiplier = newPluginJumpMultiplier;
		}

		public void sendPluginJumpMultiplier(float newPluginJumpMultiplier)
		{
			pluginJumpMultiplier = newPluginJumpMultiplier;

			if (!channel.IsLocalPlayer)
			{
				SendPluginJumpMultiplier.Invoke(GetNetId(), ENetReliability.Reliable, channel.GetOwnerTransportConnection(), newPluginJumpMultiplier);
			}
		}

		[System.Obsolete]
		public void tellPluginSpeedMultiplier(CSteamID steamID, float newPluginSpeedMultiplier)
		{
			ReceivePluginSpeedMultiplier(newPluginSpeedMultiplier);
		}

		private static readonly ClientInstanceMethod<float> SendPluginSpeedMultiplier = ClientInstanceMethod<float>.Get(typeof(PlayerMovement), nameof(ReceivePluginSpeedMultiplier));
		[SteamCall(ESteamCallValidation.ONLY_FROM_SERVER, legacyName = nameof(tellPluginSpeedMultiplier))]
		public void ReceivePluginSpeedMultiplier(float newPluginSpeedMultiplier)
		{
			pluginSpeedMultiplier = newPluginSpeedMultiplier;
		}

		public void sendPluginSpeedMultiplier(float newPluginSpeedMultiplier)
		{
			pluginSpeedMultiplier = newPluginSpeedMultiplier;

			if (!channel.IsLocalPlayer)
			{
				SendPluginSpeedMultiplier.Invoke(GetNetId(), ENetReliability.Reliable, channel.GetOwnerTransportConnection(), newPluginSpeedMultiplier);
			}
		}

		public void tellState(Vector3 newPosition, byte newPitch, byte newYaw)
		{
			if (channel.IsLocalPlayer)
			{
				return;
			}

			checkGround(newPosition);

			// We reset interpolation if it's a big skip (e.g., a culling change)
			const float LARGE_DISTANCE = 16f;
			const float SQR_LARGE_DISTANCE = LARGE_DISTANCE * LARGE_DISTANCE;
			bool isLargeDelta = (newPosition - lastUpdatePos).sqrMagnitude > SQR_LARGE_DISTANCE;

			lastUpdatePos = newPosition;
			PitchYawSnapshotInfo newSnapshot = new PitchYawSnapshotInfo(newPosition, newPitch, newYaw * 2.0f);

			if (nsb != null)
			{
				if (isLargeDelta)
				{
					nsb.updateLastSnapshot(newSnapshot);
				}
				else
				{
					nsb.addNewSnapshot(new PitchYawSnapshotInfo(newPosition, newPitch, newYaw * 2.0f));
				}
			}
#if WITH_NSB_LOGGING
			else if(!Provider.isServer)
			{
				NsbLog.WarningFormat("player tellState null buffer ({0})", channel.owner.playerID);
			}
#endif // WITH_NSB_LOGGING
		}

		public void updateMovement()
		{
			lastUpdatePos = transform.localPosition;
			if (nsb != null)
			{
				nsb.updateLastSnapshot(new PitchYawSnapshotInfo(lastUpdatePos, player.look.pitch, player.look.yaw));
			}
#if WITH_NSB_LOGGING
			else if(!Provider.isServer)
			{
				NsbLog.WarningFormat("player updateMovement null buffer ({0})", channel.owner.playerID);
			}
#endif // WITH_NSB_LOGGING

			pendingLaunchVelocity = Vector3.zero;
			velocity = Vector3.zero; // Reset velocity on death.
			mostRecentControllerColliderHit = null;
			source?.OnTeleported();
		}

		private void checkGround(Vector3 position)
		{
			materialName = null;
			materialIsWater = false;

			int mask = RayMasks.BLOCK_COLLISION;
			float CAST_RADIUS = PlayerStance.RADIUS - 0.001f;
			const float CHECK_LENGTH = SKIN_WIDTH + 0.025f;
			Ray ray = new Ray(position + new Vector3(0.0f, CAST_RADIUS, 0.0f), Vector3.down);
			Physics.SphereCast(ray, CAST_RADIUS, out ground, CHECK_LENGTH, mask, QueryTriggerInteraction.Ignore);
			_isGrounded = ground.transform != null;

			if ((channel.IsLocalPlayer || Provider.isServer) && controller.enabled && controller.isGrounded)
			{
				_isGrounded = true;
			}

			if (player.stance.stance == EPlayerStance.CLIMB || player.stance.stance == EPlayerStance.SWIM || player.stance.stance == EPlayerStance.DRIVING || player.stance.stance == EPlayerStance.SITTING)
			{
				_isGrounded = true;
			}

			if (player.stance.stance == EPlayerStance.CLIMB)
			{
				// 2021-11-16: why does climbing use tile? Sigh.
				materialName = "Tile";
			}
			else if (player.stance.stance == EPlayerStance.SWIM || SDG.Framework.Water.WaterUtility.isPointUnderwater(transform.position))
			{
				materialName = "Water";
				materialIsWater = true;
			}
			else if (ground.transform != null)
			{
				if (ground.transform.CompareTag("Ground"))
				{
					materialName = PhysicsTool.GetTerrainMaterialName(transform.position);
				}
				else
				{
					materialName = ground.collider?.sharedMaterial?.name;
				}
			}

#if WITH_MOVEMENT_GIZMOS
			Color debugColor = isGrounded ? Color.green : Color.red;
			RuntimeGizmos.Get().Spherecast(ray, CAST_RADIUS, 0.125f, debugColor, lifespan: PlayerInput.RATE);
			RuntimeGizmos.Get().Arrow(ground.point, ground.normal, 0.5f, debugColor, lifespan: PlayerInput.RATE);
#endif // WITH_MOVEMENT_GIZMOS
		}

#if !DEDICATED_SERVER
		private bool PlayLandAudioClip()
		{
			if (player.stance.stance == EPlayerStance.PRONE || string.IsNullOrEmpty(materialName))
				return false;

			OneShotAudioDefinition audioDef = PhysicMaterialCustomData.GetAudioDef(materialName, "BipedLand");
			if (audioDef == null)
				return false;

			AudioClip audioClip = audioDef.GetRandomClip();
			if (audioClip == null)
				return false;

			float volume = 1.0f - (player.skills.mastery((int) EPlayerSpeciality.DEFENSE, (int) EPlayerDefense.SNEAKYBEAKY) * 0.75f);

			if (player.stance.stance == EPlayerStance.CROUCH)
			{
				volume *= 0.5f;
			}

			volume *= 0.15f;

			OneShotAudioParameters parameters = new OneShotAudioParameters(transform, audioClip);
			parameters.volume = volume * audioDef.volumeMultiplier;
			parameters.RandomizePitch(audioDef.minPitch, audioDef.maxPitch);
			parameters.SetLinearRolloff(1.0f, 24.0f);
			parameters.Play();

#if LOG_FOOTSTEP_AUDIO
			UnturnedLog.info($"Play material \"{materialName}\" BipedLand audio");
#endif

			lastFootstep = Time.time;
			lastFootstepPosition = transform.position;

			return true;
		}

		private void PlayFootstepAudioClip()
		{
			string key = player.stance.stance == EPlayerStance.SPRINT ? "FootstepRun" : "FootstepWalk";
			OneShotAudioDefinition audioDef = PhysicMaterialCustomData.GetAudioDef(materialName, key);
			if (audioDef == null)
				return;

			AudioClip audioClip = audioDef.GetRandomClip();
			if (audioClip == null)
				return;

			float volume = 1f - (player.skills.mastery((int) EPlayerSpeciality.DEFENSE, (int) EPlayerDefense.SNEAKYBEAKY) * 0.75f);

			if (player.stance.stance == EPlayerStance.CROUCH)
			{
				volume *= 0.5f;
			}

			volume *= 0.125f;

			OneShotAudioParameters parameters = new OneShotAudioParameters(transform, audioClip);
			parameters.volume = volume * audioDef.volumeMultiplier;
			parameters.RandomizePitch(audioDef.minPitch, audioDef.maxPitch);
			parameters.SetLinearRolloff(1.0f, 32.0f);
			parameters.Play();

#if LOG_FOOTSTEP_AUDIO
			UnturnedLog.info($"Play material \"{materialName}\" {key} audio");
#endif
		}

		private static MasterBundleReference<OneShotAudioDefinition> lightWadingAudioRef = new MasterBundleReference<OneShotAudioDefinition>("core.masterbundle", "Effects/Physics/Swim/LightWading/Swim_LightWading.asset");
		private static MasterBundleReference<OneShotAudioDefinition> mediumWadingAudioRef = new MasterBundleReference<OneShotAudioDefinition>("core.masterbundle", "Effects/Physics/Swim/MediumWading/Swim_MediumWading.asset");
		private static MasterBundleReference<OneShotAudioDefinition> heavyWadingAudioRef = new MasterBundleReference<OneShotAudioDefinition>("core.masterbundle", "Effects/Physics/Swim/HeavyWading/Swim_HeavyWading.asset");

		internal void PlaySwimAudioClip()
		{
			OneShotAudioDefinition audioDef;
			if (player.stance.stance == EPlayerStance.SWIM)
			{
				if (player.stance.areEyesUnderwater)
				{
					// This clip sounds the most like underwater swimming to me.
					audioDef = mediumWadingAudioRef.loadAsset();
					if (audioDef == null)
					{
						UnturnedLog.warn("Missing built-in medium wading audio");
					}

#if LOG_FOOTSTEP_AUDIO
					UnturnedLog.info($"Play swimming medium wading audio (position {transform.position} eyes underwater)");
#endif
				}
				else
				{
					// Treading water at the surface.
					audioDef = heavyWadingAudioRef.loadAsset();
					if (audioDef == null)
					{
						UnturnedLog.warn("Missing built-in heavy wading audio");
					}

#if LOG_FOOTSTEP_AUDIO
					UnturnedLog.info($"Play swimming heavy wading audio (position {transform.position} eyes aren't underwater)");
#endif
				}
			}
			else
			{
				// Swim stance threshold is 1.25m, so use a midpoint for light wading.
				if (SDG.Framework.Water.WaterUtility.isPointUnderwater(transform.position + new Vector3(0.0f, 0.5f, 0.0f)))
				{
					audioDef = lightWadingAudioRef.loadAsset();
					if (audioDef == null)
					{
						UnturnedLog.warn("Missing built-in light wading audio");
					}

#if LOG_FOOTSTEP_AUDIO
					UnturnedLog.info($"Play near-swimming light wading audio (position {transform.position} body underwater)");
#endif
				}
				else
				{
					// Just splashing.
					string key = player.stance.stance == EPlayerStance.SPRINT ? "FootstepRun" : "FootstepWalk";
					audioDef = PhysicMaterialCustomData.GetAudioDef("Water", key);

#if LOG_FOOTSTEP_AUDIO
					UnturnedLog.info($"Play material Water {key} audio");
#endif
				}
			}

			if (audioDef == null)
				return;

			AudioClip audioClip = audioDef.GetRandomClip();
			if (audioClip == null)
				return;

			float volume = 0.15f;
			if (player.stance.stance == EPlayerStance.CROUCH)
			{
				volume *= 0.5f;
			}

			OneShotAudioParameters parameters = new OneShotAudioParameters(transform, audioClip);
			parameters.volume = volume * audioDef.volumeMultiplier;
			parameters.RandomizePitch(audioDef.minPitch, audioDef.maxPitch);
			parameters.SetLinearRolloff(1.0f, 32.0f);
			parameters.Play();
		}
#endif // !DEDICATED_SERVER

		private void onVisionUpdated(bool isViewing)
		{
			if (isViewing)
			{
				warp_x = Random.value < 0.25 ? -1 : 1;
				warp_y = Random.value < 0.25 ? -1 : 1;
			}
			else
			{
				warp_x = 1;
				warp_y = 1;
			}
		}

		/// <summary>
		/// Serverside force player to exit vehicle regardless of safe exit points.
		/// </summary>
		/// <returns>True if player was seated in vehicle.</returns>
		public bool forceRemoveFromVehicle()
		{
			if (vehicle != null && channel != null && channel.owner != null)
			{
				byte seat;
				Vector3 point;
				byte angle;
				if (vehicle.forceRemovePlayer(out seat, channel.owner.playerID.steamID, out point, out angle))
				{
					VehicleManager.sendExitVehicle(vehicle, seat, point, angle, true);
					return true;
				}
			}

			// If prior to death player was entering vehicle, then cancel and notify clients.
			if (hasPendingVehicleChange && pendingVehicle != null)
			{
				byte angle = MeasurementTool.angleToByte(transform.rotation.eulerAngles.y);
				VehicleManager.sendExitVehicle(pendingVehicle, pendingSeatIndex, transform.position, angle, true);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
				UnityEngine.Assertions.Assert.IsNull(pendingVehicle);
#endif
				return true;
			}

			return false;
		}

		/// <summary>
		/// Dedicated server simulate while input queue is empty.
		/// </summary>
		public void simulate()
		{
			updateRegionAndBound();

			if (channel.IsLocalPlayer)
			{
				lastUpdatePos = transform.position;
			}

			if (hasPendingVehicleChange)
			{
				ApplyPendingVehicleChange();
				return;
			}
		}

		/// <summary>
		/// Dedicated server simulate driving input.
		/// </summary>
		public void simulate(uint simulation, int recov, bool inputBrake, bool inputStamina, Vector3 point, Quaternion rotation, float newSpeed, float newForwardVelocity, float newSteeringInput, float newVelocityInput, float delta)
		{
			updateRegionAndBound();

			if (channel.IsLocalPlayer)
			{
				lastUpdatePos = transform.position;
			}

			velocity = Vector3.zero;
			pendingLaunchVelocity = Vector3.zero;
			mostRecentControllerColliderHit = null;

			if (hasPendingVehicleChange)
			{
				ApplyPendingVehicleChange();
				return;
			}

			if (player.stance.stance == EPlayerStance.DRIVING)
			{
				ServerUpdateTurretAim();

				if (vehicle != null)
				{
					vehicle.simulate(simulation, recov, inputStamina, point, rotation, newSpeed, newForwardVelocity, newSteeringInput, newVelocityInput, delta);
				}
			}
		}

		private void ServerUpdateTurretAim()
		{
			Passenger seatObject = getVehicleSeat();
			if (seatObject?.turret != null)
			{
				if (Mathf.Abs(player.look.lastAngle - player.look.angle) > 1 || Mathf.Abs(player.look.lastRot - player.look.rot) > 1)
				{
					player.look.lastAngle = player.look.angle;
					player.look.lastRot = player.look.rot;

					if (canAddSimulationResultsToUpdates)
					{
						mostRecentlyAddedUpdate = new PlayerStateUpdate(transform.position, player.look.angle, player.look.rot);
						hasMostRecentlyAddedUpdate = true;
						updates.Add(mostRecentlyAddedUpdate);
					}
				}
			}
		}

		/// <summary>
		/// Client and dedicated server simulate walking input.
		/// </summary>
		public void simulate(uint simulation, int recov, int input_x, int input_y, float look_x, float look_y, bool inputJump, bool inputSprint, float deltaTime)
		{
			updateRegionAndBound();

			if (channel.IsLocalPlayer)
			{
				lastUpdatePos = transform.position;
			}

			if (hasPendingVehicleChange)
			{
				ApplyPendingVehicleChange();
				// this does not return, as pausing movement will be handled by the waiting-for-teleport section.
			}

			_move.x = input_x;
			_move.z = input_y;

			if (player.stance.stance == EPlayerStance.SITTING)
			{
				_isMoving = false;
				checkGround(transform.position);
				mostRecentControllerColliderHit = null;

				velocity = Vector3.zero;
				pendingLaunchVelocity = Vector3.zero;

				ServerUpdateTurretAim();
				return;
			}
			else if (player.stance.stance == EPlayerStance.DRIVING)
			{
				_isMoving = false;
				checkGround(transform.position);
				mostRecentControllerColliderHit = null;

				velocity = Vector3.zero;
				pendingLaunchVelocity = Vector3.zero;

				if (channel.IsLocalPlayer)
				{
					vehicle.simulate(simulation, recov, input_x, input_y, look_x, look_y, inputJump, inputSprint, deltaTime);

					if (onVehicleUpdated != null)
					{
						ushort displayCurrentFuel;
						ushort displayMaxFuel;
						vehicle.getDisplayFuel(out displayCurrentFuel, out displayMaxFuel);

						onVehicleUpdated(!vehicle.isUnderwater && !vehicle.isDead, displayCurrentFuel, displayMaxFuel, vehicle.ReplicatedForwardVelocity, vehicle.asset.TargetReverseVelocity, vehicle.asset.TargetForwardVelocity, vehicle.health, vehicle.asset.health, vehicle.batteryCharge);
					}
				}

				return;
			}
			else
			{
				// Walking, ladder and swimming physics run once per 100 Hz command in RunSourceCmd (predicted by the
				// client in Update, replayed by the server from the input packet). This 12.5 Hz tick only refreshes
				// the ground material used for footsteps, fall damage and surface friction.
				_isMoving = Mathf.Abs(move.x) > 0.1 || Mathf.Abs(move.z) > 0.1;
				checkGround(transform.position);
				if (source != null && player.stance.stance != EPlayerStance.CLIMB && player.stance.stance != EPlayerStance.SWIM)
				{
					_isGrounded = source.onGround;
					source.RefreshGroundMaterial(materialName);
				}
			}

			if (Level.info != null && Level.info.configData.Use_Legacy_Clip_Borders)
			{
				Vector3 clampedPosition = transform.position;

				// Clamp players into horizontal bounds, but only within a certain range because
				// technically the Russia lab is outside the map border.
				float borderXZMin = (Level.size / 2.0f) - Level.border;
				float borderXZMax = borderXZMin + 8.0f;

				bool bPushedIntoBounds = false;

				if (clampedPosition.x > -borderXZMax && clampedPosition.x < -borderXZMin)
				{
					clampedPosition.x = -borderXZMin + 1.0f;
					bPushedIntoBounds = true;
				}
				else if (clampedPosition.x < borderXZMax && clampedPosition.x > borderXZMin)
				{
					clampedPosition.x = borderXZMin - 1.0f;
					bPushedIntoBounds = true;
				}

				if (clampedPosition.z > -borderXZMax && clampedPosition.z < -borderXZMin)
				{
					clampedPosition.z = -borderXZMin + 1.0f;
					bPushedIntoBounds = true;
				}
				else if (clampedPosition.z < borderXZMax && clampedPosition.z > borderXZMin)
				{
					clampedPosition.z = borderXZMin - 1.0f;
					bPushedIntoBounds = true;
				}

				if (bPushedIntoBounds)
				{
					// Lift up to help prevent falling into ground.
					clampedPosition.y += 8.0f;
				}

				// Caps are 8m tall and offset +/-4m from the level bounds.
				clampedPosition.y = Mathf.Clamp(clampedPosition.y, 0, Level.HEIGHT);

				transform.position = clampedPosition;
			}

			// Only server can perform ground adjustment because bypassUndergroundWhitelist flag is not replicated.
			if (Provider.isServer)
			{
				// Player is admin in singleplayer, so we only allow maintenance access on dedicated servers.
				bool bypassWhitelist = bypassUndergroundWhitelist || (Dedicator.IsDedicatedServer && channel.owner.isAdmin);
				if (!bypassWhitelist)
				{
					Vector3 adjustedPosition = transform.position;
					if (UndergroundAllowlist.AdjustPosition(ref adjustedPosition, 0.5f))
					{
						transform.position = adjustedPosition;
					}
				}
			}

			// After level clamping and ground adjustment because those affect server result.
			if (!channel.IsLocalPlayer && Provider.isServer)
			{
				if (updates != null)
				{
					Vector3 currentPos = transform.position;
					if (Mathf.Abs(player.look.lastAngle - player.look.angle) > 1 || Mathf.Abs(player.look.lastRot - player.look.rot) > 1 || Mathf.Abs(lastUpdatePos.x - currentPos.x) > Provider.UPDATE_DISTANCE || Mathf.Abs(lastUpdatePos.y - currentPos.y) > Provider.UPDATE_DISTANCE || Mathf.Abs(lastUpdatePos.z - currentPos.z) > Provider.UPDATE_DISTANCE)
					{
						player.look.lastAngle = player.look.angle;
						player.look.lastRot = player.look.rot;
						lastUpdatePos = currentPos;

						if (canAddSimulationResultsToUpdates)
						{
							mostRecentlyAddedUpdate = new PlayerStateUpdate(currentPos, player.look.angle, player.look.rot);
							hasMostRecentlyAddedUpdate = true;
							updates.Add(mostRecentlyAddedUpdate);
						}
					}
				}
			}
		}

		#region CS:S 100 tick movement

		/// <summary>
		/// Upper bound on 100 Hz ticks the client simulates in a single frame after a hitch.
		/// </summary>
		private const int MAX_CLIENT_SOURCE_TICKS_PER_FRAME = 25;

		/// <summary>
		/// Upper bound on predicted-but-unsent commands. Normally emptied every 80 ms by PlayerInput.
		/// </summary>
		private const int MAX_CLIENT_PENDING_SOURCE_CMDS = 256;

		/// <summary>
		/// CS:S movement state. Only exists for the local player and on the server.
		/// </summary>
		internal SourceMovement source { get; private set; }

		/// <summary>
		/// Local player only: mouse turn rate vs ideal strafe turn rate, for the HUD.
		/// </summary>
		internal readonly StrafeTrainer strafeTrainer = new StrafeTrainer();

		private readonly List<SourceUserCmd> clientPendingSourceCmds = new List<SourceUserCmd>();
		private float clientSourceTickAccumulator;
		private bool clientHasSourceInterpolation;
		private Vector3 clientSourceInterpolationFrom;
		private Vector3 clientSourceInterpolationTo;
		private Vector3 lastFootstepPosition;

		/// <summary>
		/// CS:S ticks move the transform directly, so the CharacterController needs syncing before the
		/// ladder or swimming code moves it again.
		/// </summary>
		private bool sourceMovedTransformDirectly;

		internal bool CanRunSourceCmd => source != null
			&& controller != null
			&& controller.enabled
			&& vehicle == null
			&& !hasPendingVehicleChange
			&& player.life.IsAlive
			&& player.stance.stance != EPlayerStance.SITTING
			&& player.stance.stance != EPlayerStance.DRIVING;

		/// <summary>
		/// Simulate one 100 Hz command. Shared by client prediction, client resimulation and server replay,
		/// so it must only depend on the command and replicated state.
		/// </summary>
		internal void RunSourceCmd(in SourceUserCmd cmd)
		{
			if (!CanRunSourceCmd)
				return;

			_move.x = cmd.MoveX;
			_move.z = cmd.MoveZ;
			_isMoving = cmd.MoveX != 0 || cmd.MoveZ != 0;
			source.tickOriginShift = 0.0f;
			source.strafeSampleValid = false;

			EPlayerStance stance = player.stance.stance;
			if (stance == EPlayerStance.CLIMB || stance == EPlayerStance.SWIM)
			{
				if (sourceMovedTransformDirectly)
				{
					Physics.SyncTransforms();
					sourceMovedTransformDirectly = false;
				}

				mostRecentControllerColliderHit = null;
				if (stance == EPlayerStance.CLIMB)
				{
					SimulateLegacyClimbTick(cmd);
				}
				else
				{
					SimulateLegacySwimTick(cmd);
				}

				_isGrounded = true;
				source.OnLegacyTick(cmd);
			}
			else
			{
				source.Tick(cmd);
				sourceMovedTransformDirectly = true;
				_isGrounded = source.onGround;
			}
		}

		/// <summary>
		/// Unturned's ladder movement, unchanged apart from running per 100 Hz command.
		/// </summary>
		private void SimulateLegacyClimbTick(in SourceUserCmd cmd)
		{
			// Nelson 2025-09-01: when air acceleration is low the player isn't necessarily able to build up enough
			// speed to move off the ladder and onto a surface in front, so queue up a small forward boost.
			if (move.z > 0.1f)
			{
				pendingLaunchVelocity = Quaternion.Euler(0.0f, cmd.Yaw, 0.0f) * new Vector3(0.0f, 0.0f, 0.5f);
			}
			else
			{
				pendingLaunchVelocity = Vector3.zero;
			}

			velocity = new Vector3(0.0f, _move.z * speed * 0.5f, 0.0f);
			controller.CheckedMove(velocity * SourceMovement.TICK_INTERVAL);
		}

		/// <summary>
		/// Unturned's swimming movement, unchanged apart from running per 100 Hz command.
		/// </summary>
		private void SimulateLegacySwimTick(in SourceUserCmd cmd)
		{
			pendingLaunchVelocity = Vector3.zero;

			Quaternion yawRotation = Quaternion.Euler(0.0f, cmd.Yaw, 0.0f);
			if (player.stance.isSubmerged || (cmd.Pitch > 110 && move.z > 0.1))
			{
				Quaternion aimRotation = yawRotation * Quaternion.Euler(cmd.Pitch - 90.0f, 0.0f, 0.0f);
				velocity = aimRotation * move.normalized * speed * 1.5f;
				if (cmd.IsHeld(SourceUserCmd.IN_JUMP))
				{
					velocity.y = SWIM * pluginJumpMultiplier;
				}
			}
			else
			{
				// Swimming along surface
				WaterUtility.getUnderwaterInfo(transform.position, out bool isUnderwater, out float surfaceElevation);
				velocity = yawRotation * move.normalized * speed * 1.5f;
				velocity.y = (surfaceElevation - 1.275f - transform.position.y) / 8f;
			}

			controller.CheckedMove(velocity * SourceMovement.TICK_INTERVAL);
		}

		/// <summary>
		/// Server replays the commands from an input packet. (Count was already limited by PlayerInput.)
		/// Client resimulation replays commands from input history.
		/// </summary>
		internal void SimulateSourceCmds(SourceUserCmd[] cmds, int count)
		{
			if (cmds == null)
				return;

			count = Mathf.Min(count, cmds.Length);
			for (int index = 0; index < count; ++index)
			{
				RunSourceCmd(cmds[index]);
			}
		}

		/// <summary>
		/// Take up to one packet's worth of commands predicted since the previous packet.
		/// </summary>
		internal SourceUserCmd[] ClientFlushSourceCmds(int maxCount)
		{
			int count = Mathf.Min(clientPendingSourceCmds.Count, maxCount);
			if (count <= 0)
				return System.Array.Empty<SourceUserCmd>();

			SourceUserCmd[] cmds = new SourceUserCmd[count];
			clientPendingSourceCmds.CopyTo(0, cmds, 0, count);
			clientPendingSourceCmds.RemoveRange(0, count);
			return cmds;
		}

		/// <summary>
		/// After resimulating input history, re-apply commands predicted since the most recent packet.
		/// </summary>
		internal void ClientReplayPendingSourceCmds()
		{
			foreach (SourceUserCmd cmd in clientPendingSourceCmds)
			{
				RunSourceCmd(cmd);
			}

			// Snap interpolation to the corrected position.
			clientHasSourceInterpolation = false;
		}

		private SourceUserCmd ClientBuildSourceCmd()
		{
			SourceUserCmd cmd;
			cmd.packedYaw = SourceUserCmd.PackYaw(player.look.yaw);
			cmd.packedPitch = SourceUserCmd.PackPitch(player.look.pitch);

			byte buttons = 0;
			int moveX = _horizontal - 1;
			int moveZ = _vertical - 1;
			if (moveZ > 0)
			{
				buttons |= SourceUserCmd.IN_FORWARD;
			}
			else if (moveZ < 0)
			{
				buttons |= SourceUserCmd.IN_BACK;
			}

			if (moveX > 0)
			{
				buttons |= SourceUserCmd.IN_MOVERIGHT;
			}
			else if (moveX < 0)
			{
				buttons |= SourceUserCmd.IN_MOVELEFT;
			}

			if (_jump)
			{
				buttons |= SourceUserCmd.IN_JUMP;
			}

			// Players with crouch set to hold mode should stay crouched while in the rest gesture.
			if (player.stance.crouch || (player.animator.gesture == EPlayerGesture.REST_START && !player.stance.prone))
			{
				buttons |= SourceUserCmd.IN_DUCK;
			}

			// Sprint key is the CS:S walk key.
			if (player.stance.sprint)
			{
				buttons |= SourceUserCmd.IN_SPEED;
			}

			cmd.buttons = buttons;
			return cmd;
		}

		/// <summary>
		/// Client samples and predicts one command every 10 ms of frame time, like a Source client running
		/// usercmds at tickrate 100. Rendering interpolates between the two most recent ticks.
		/// </summary>
		private void ClientUpdateSourceMovement()
		{
			if (!CanRunSourceCmd || LoadingUI.isBlocked)
			{
				clientSourceTickAccumulator = 0.0f;
				clientHasSourceInterpolation = false;
				strafeTrainer.Reset();
				return;
			}

			Vector3 position = transform.position;
			if (!clientHasSourceInterpolation || (position - clientSourceInterpolationTo).sqrMagnitude > 1e-8f)
			{
				// First tick, or something besides the 100 Hz ticks moved us (teleport, server correction, ladder snap).
				clientSourceInterpolationFrom = position;
				clientSourceInterpolationTo = position;
				clientHasSourceInterpolation = true;
			}

			clientSourceTickAccumulator += Time.deltaTime;
			clientSourceTickAccumulator = Mathf.Min(clientSourceTickAccumulator, MAX_CLIENT_SOURCE_TICKS_PER_FRAME * SourceMovement.TICK_INTERVAL);

			while (clientSourceTickAccumulator >= SourceMovement.TICK_INTERVAL)
			{
				clientSourceTickAccumulator -= SourceMovement.TICK_INTERVAL;

				SourceUserCmd cmd = ClientBuildSourceCmd();
				Vector3 before = transform.position;
				RunSourceCmd(cmd);
				strafeTrainer.AddTick(source, cmd.Yaw);

				if (clientPendingSourceCmds.Count >= MAX_CLIENT_PENDING_SOURCE_CMDS)
				{
					clientPendingSourceCmds.RemoveAt(0);
				}
				clientPendingSourceCmds.Add(cmd);

				clientSourceInterpolationFrom = before + (Vector3.up * source.tickOriginShift);
				clientSourceInterpolationTo = transform.position;

				if (!CanRunSourceCmd)
				{
					// E.g., died or entered a vehicle.
					clientSourceTickAccumulator = 0.0f;
					clientHasSourceInterpolation = false;
					break;
				}
			}
		}

		/// <summary>
		/// Called by SourceMovement when the hull lands.
		/// </summary>
		internal void NotifySourceLanded(float verticalVelocity)
		{
			checkGround(transform.position);
			_isGrounded = true;

			onLanded?.Invoke(verticalVelocity);

#if !DEDICATED_SERVER
			if (!player.input.isResimulating && Mathf.Abs(verticalVelocity) > 1.0f)
			{
				PlayLandAudioClip();
			}
#endif // !DEDICATED_SERVER
		}

		/// <summary>
		/// Called by SourceMovement when ducking or unducking in the air moved the origin to keep the head in place.
		/// </summary>
		internal void OnSourceDuckOriginShift(float delta)
		{
			if (channel.IsLocalPlayer && !player.input.isResimulating)
			{
				player.look.SourceAdjustEyeHeight(-delta);
			}
		}

		#endregion

		private void Update()
		{
			if (nsb != null)
			{
				snapshot = nsb.getCurrentSnapshot();
			}
#if WITH_NSB_LOGGING
			else if(!Provider.isServer)
			{
				NsbLog.WarningFormat("player Update null buffer ({0})", channel.owner.playerID);
			}
#endif // WITH_NSB_LOGGING

			UnityEngine.Profiling.Profiler.BeginSample("Input");

			if (channel.IsLocalPlayer)
			{
				if (!PlayerUI.window.showCursor && !LoadingUI.isBlocked)
				{
					_jump = InputEx.GetKey(ControlsSettings.jump);

					if (getVehicle() != null)
					{
						if (InputEx.GetKeyDown(ControlsSettings.locker))
						{
							VehicleManager.sendVehicleLock();
						}

						if (InputEx.GetKeyDown(ControlsSettings.primary))
						{
							VehicleManager.sendVehicleHorn();
						}

						if (InputEx.GetKeyDown(ControlsSettings.secondary))
						{
							VehicleManager.sendVehicleHeadlights();
						}

						if (InputEx.GetKeyDown(ControlsSettings.other))
						{
							VehicleManager.sendVehicleBonus();
						}
					}

					if (getVehicle() != null && getVehicle().asset != null && (getVehicle().asset.engine == EEngine.PLANE || getVehicle().asset.engine == EEngine.HELICOPTER || getVehicle().asset.engine == EEngine.BLIMP))
					{
						if (InputEx.GetKey(ControlsSettings.yawLeft))
						{
							input_x = -1;
						}
						else if (InputEx.GetKey(ControlsSettings.yawRight))
						{
							input_x = 1;
						}
						else
						{
							input_x = 0;
						}

						if (InputEx.GetKey(ControlsSettings.thrustIncrease))
						{
							input_y = 1;
						}
						else if (InputEx.GetKey(ControlsSettings.thrustDecrease))
						{
							input_y = -1;
						}
						else
						{
							input_y = 0;
						}
					}
					else
					{
						if (InputEx.GetKey(ControlsSettings.left))
						{
							input_x = -1;
						}
						else if (InputEx.GetKey(ControlsSettings.right))
						{
							input_x = 1;
						}
						else
						{
							input_x = 0;
						}

						if (InputEx.GetKey(ControlsSettings.up))
						{
							input_y = 1;
						}
						else if (InputEx.GetKey(ControlsSettings.down))
						{
							input_y = -1;
						}
						else
						{
							input_y = 0;
						}
					}
				}
				else
				{
					_jump = false;

					input_x = 0;
					input_y = 0;
				}

				input_x *= warp_x;
				input_y *= warp_y;

				if (player.look.IsControllingFreecam)
				{
					_jump = false;
					_horizontal = 1;
					_vertical = 1;
				}
				else
				{
					_horizontal = (byte) (input_x + 1);
					_vertical = (byte) (input_y + 1);
				}

				ClientUpdateSourceMovement();
			}

			UnityEngine.Profiling.Profiler.EndSample();
			UnityEngine.Profiling.Profiler.BeginSample("Footsteps");

#if !DEDICATED_SERVER
			if (!Dedicator.IsDedicatedServer)
			{
				if (Time.time - lastFootstep > 2.1f / speed)
				{
					// CS:S footsteps are silent below a speed threshold, which keeps walk-key and crouch movement quiet.
					// Measured from position so it works for remote players too.
					float footstepElapsed = Time.time - lastFootstep;
					float footstepHorizontalSpeed = footstepElapsed > 0.0f ? (transform.position - lastFootstepPosition).GetHorizontalMagnitude() / footstepElapsed : 0.0f;
					bool isFootstepAudible = footstepHorizontalSpeed >= SourceMovement.FOOTSTEP_MIN_SPEED * SourceMovement.METERS_PER_UNIT
						|| player.stance.stance == EPlayerStance.CLIMB;

					lastFootstep = Time.time;
					lastFootstepPosition = transform.position;

					bool playedLand = false;

					if (!channel.IsLocalPlayer)
					{
						bool wasGrounded = isGrounded;
						checkGround(transform.position);
						if (!wasGrounded && isGrounded)
						{
							playedLand = PlayLandAudioClip();
						}
					}

					if (isGrounded && !playedLand)
					{
						if (isMoving && player.stance.stance != EPlayerStance.PRONE)
						{
							if (materialIsWater || player.stance.stance == EPlayerStance.SWIM)
							{
								PlaySwimAudioClip();
							}
							else if (!string.IsNullOrEmpty(materialName) && isFootstepAudible)
							{
								PlayFootstepAudioClip();
							}
						}
					}
				}
			}
#endif // !DEDICATED_SERVER

			UnityEngine.Profiling.Profiler.EndSample();

			if (channel.IsLocalPlayer)
			{
				UnityEngine.Profiling.Profiler.BeginSample("Orbit");

				if (player.look.IsControllingFreecam)
				{
					if (!player.workzone.isBuilding || InputEx.GetKey(ControlsSettings.secondary))
					{
						bool acceptInput = !PlayerUI.window.showCursor;
						float scrollWheelInput = acceptInput ? Input.GetAxis("mouse_z") : 0.0f;

						if (InputEx.GetKey(ControlsSettings.other))
						{
							if (player.look.freecamVerticalFieldOfView > 0.0f)
							{
								player.look.freecamVerticalFieldOfView = Mathf.Clamp(player.look.freecamVerticalFieldOfView + scrollWheelInput * 5.0f, 1.0f, 179.0f);
							}
						}
						else
						{
							player.look.orbitSpeed = Mathf.Clamp(player.look.orbitSpeed + (scrollWheelInput * 0.2f * player.look.orbitSpeed), 0.5f, 2048);
						}

						Vector3 orbitHorizontalMotion = MainCamera.instance.transform.right * input_x * Time.deltaTime * player.look.orbitSpeed;
						if (player.look.isFocusing)
						{
							// Nelson 2024-06-11: Fixes drifting closer or further away while spinning around the focal
							// point. (public issue #4487)
							Vector3 focalPoint = player.first.position + Vector3.up;
							Vector3 oldCameraPosition = player.look.lockPosition + player.look.orbitPosition;
							Vector3 oldFocusVector = focalPoint - oldCameraPosition;
							float oldHorizontalMagnitude = oldFocusVector.GetHorizontalMagnitude();

							Vector3 newOrbitPosition = player.look.orbitPosition + orbitHorizontalMotion;
							Vector3 newCameraPosition = player.look.lockPosition + newOrbitPosition;
							Vector3 newFocusVector = focalPoint - newCameraPosition;
							float newHorizontalMagnitude = newFocusVector.GetHorizontalMagnitude();
							if (newHorizontalMagnitude < 0.001f)
							{
								// Prevent division by zero;
								newHorizontalMagnitude = 1.0f;
							}

							float scaleFactor = oldHorizontalMagnitude / newHorizontalMagnitude;
							newOrbitPosition.x *= scaleFactor;
							newOrbitPosition.z *= scaleFactor;
							player.look.orbitPosition = newOrbitPosition;

							Debug.Assert(MathfEx.IsNearlyEqual(oldHorizontalMagnitude, (focalPoint - (player.look.lockPosition + player.look.orbitPosition)).GetHorizontalMagnitude()));
						}
						else
						{
							player.look.orbitPosition += orbitHorizontalMotion;
						}
						player.look.orbitPosition += MainCamera.instance.transform.forward * input_y * Time.deltaTime * player.look.orbitSpeed;

						float height = 0.0f;
						if (acceptInput)
						{
							if (InputEx.GetKey(ControlsSettings.ascend))
							{
								height = 1.0f;
							}
							else if (InputEx.GetKey(ControlsSettings.descend))
							{
								height = -1.0f;
							}
						}

						player.look.orbitPosition += Vector3.up * height * Time.deltaTime * player.look.orbitSpeed;
					}
				}

				UnityEngine.Profiling.Profiler.EndSample();
				UnityEngine.Profiling.Profiler.BeginSample("Lerp");

				if (player.stance.stance == EPlayerStance.DRIVING || player.stance.stance == EPlayerStance.SITTING)
				{
					player.first.localPosition = Vector3.zero;
					player.third.localPosition = Vector3.zero;
				}
				else if (clientHasSourceInterpolation)
				{
					// Interpolate between the two most recent 100 Hz ticks.
					float alpha = Mathf.Clamp01(clientSourceTickAccumulator / SourceMovement.TICK_INTERVAL);
					player.first.position = Vector3.Lerp(clientSourceInterpolationFrom, clientSourceInterpolationTo, alpha);
					player.third.position = player.first.position;
				}
				else
				{
					player.first.position = Vector3.Lerp(lastUpdatePos, transform.position, (Time.realtimeSinceStartup - player.input.tick) / PlayerInput.RATE);
					// Nelson 2024-08-19: Previously, this pushed the first-person position down by 10 cm while prone.
					// I think we can remove that offset now that the character is snapped to the ground.
					player.third.position = player.first.position;
				}

				player.look.aim.parent.transform.position = player.first.position;//Vector3.Lerp(position, transform.position, (Time.realtimeSinceStartup-player.input.tick)/PlayerInput.RATE);//player.animator.hook.transform.position;

				UnityEngine.Profiling.Profiler.EndSample();
				UnityEngine.Profiling.Profiler.BeginSample("Stats");

				if (vehicle != null)
				{
					if ((transform.position - lastStatPos).sqrMagnitude > 1024)
					{
						lastStatPos = transform.position;
					}
					else if (Time.realtimeSinceStartup - lastStatTime > 1)
					{
						lastStatTime = Time.realtimeSinceStartup;

						if ((transform.position - lastStatPos).sqrMagnitude > 0.1f)
						{
							int data;
							if (Provider.provider.statisticsService.userStatisticsService.getStatistic("Travel_Vehicle", out data))
							{
								Provider.provider.statisticsService.userStatisticsService.setStatistic("Travel_Vehicle", data + (int) (transform.position - lastStatPos).magnitude);
							}

							lastStatPos = transform.position;
						}
					}
				}
				else
				{
					if ((transform.position - lastStatPos).sqrMagnitude > 256)
					{
						lastStatPos = transform.position;
					}
					else if (Time.realtimeSinceStartup - lastStatTime > 1)
					{
						lastStatTime = Time.realtimeSinceStartup;

						if ((transform.position - lastStatPos).sqrMagnitude > 0.1f)
						{
							int data;
							if (Provider.provider.statisticsService.userStatisticsService.getStatistic("Travel_Foot", out data))
							{
								Provider.provider.statisticsService.userStatisticsService.setStatistic("Travel_Foot", data + (int) (transform.position - lastStatPos).magnitude);
							}

							lastStatPos = transform.position;
						}
					}
				}

				UnityEngine.Profiling.Profiler.EndSample();
			}
			else if (!Provider.isServer)
			{
				if (player.stance.stance == EPlayerStance.SITTING || player.stance.stance == EPlayerStance.DRIVING)
				{
					_isMoving = false;

					transform.localPosition = Vector3.zero;
				}
				else
				{
					if (Mathf.Abs(lastUpdatePos.x - transform.position.x) > 0.01f || Mathf.Abs(lastUpdatePos.y - transform.position.y) > 0.01f || Mathf.Abs(lastUpdatePos.z - transform.position.z) > 0.01f)
					{
						_isMoving = true;
					}
					else
					{
						_isMoving = false;
					}

					transform.localPosition = snapshot.pos;
				}
			}

			if (!channel.IsLocalPlayer)
			{
				if (player.third != null)
				{
					if (player.stance.stance == EPlayerStance.PRONE)
					{
						player.third.localPosition = new Vector3(0.0f, -0.1f, 0.0f);
					}
					else
					{
						player.third.localPosition = Vector3.zero;
					}
				}
			}
		}

		private void updateRegionAndBound()
		{
			byte new_x;
			byte new_y;

			if (Regions.tryGetCoordinate(transform.position, out new_x, out new_y))
			{
				if (new_x != region_x || new_y != region_y)
				{
					byte old_x = region_x;
					byte old_y = region_y;

					_region_x = new_x;
					_region_y = new_y;

					updateRegionOld_X = old_x;
					updateRegionOld_Y = old_y;
					updateRegionNew_X = new_x;
					updateRegionNew_Y = new_y;
					updateRegionIndex = 0;
				}
			}

			if (updateRegionIndex < 6)
			{
				bool canIncrementIndex = true;

				onRegionUpdated?.Invoke(player, updateRegionOld_X, updateRegionOld_Y, updateRegionNew_X, updateRegionNew_Y, updateRegionIndex, ref canIncrementIndex);

				if (canIncrementIndex)
				{
					updateRegionIndex++;
				}
			}

			byte newBound;
			LevelNavigation.tryGetBounds(transform.position, out newBound);

			if (newBound != bound)
			{
				byte oldBound = bound;

				_bound = newBound;

				onBoundUpdated?.Invoke(player, oldBound, newBound);
			}

			if (Provider.isServer)
			{
				byte newNav;
				LevelNavigation.tryGetNavigation(transform.position, out newNav);

				if (newNav != nav)
				{
					byte oldNav = nav;
					_nav = newNav;

					TriggerPlayerNavChanged(oldNav, newNav);
				}
			}

			bool newSafe = false;
			newSafe = LevelNodes.isPointInsideSafezone(transform.position, out isSafeInfo);

			bool newRadiated = false;
			IDeadzoneNode newDeadzoneInfo = null;

			HordePurchaseVolume newPurchaseNode = HordePurchaseVolumeManager.Get().GetFirstOverlappingVolume(transform.position);
			effectNode = null;
			inSnow = LevelLighting.isPositionSnowy(transform.position);

			SDG.Framework.Devkit.AmbianceVolume ambianceVolume = SDG.Framework.Devkit.AmbianceVolumeManager.Get().GetFirstOverlappingVolume(transform.position);
			if (ambianceVolume != null)
			{
				effectNode = ambianceVolume;

				if (!inSnow && Level.info != null && Level.info.configData.Use_Snow_Volumes)
				{
					inSnow = (ambianceVolume.weatherMask & (1U << 1)) != 0;
				}

				WeatherMask = ambianceVolume.weatherMask;
			}
			else
			{
				LevelAsset levelAsset = Level.getAsset();
				WeatherMask = levelAsset != null ? levelAsset.globalWeatherMask : uint.MaxValue;
			}

			inSnow &= LevelLighting.snowyness == ELightingSnow.BLIZZARD;

			SDG.Framework.Devkit.DeadzoneVolume deadzoneVolume = DeadzoneVolumeManager.Get().GetMostDangerousOverlappingVolume(transform.position);
			if (deadzoneVolume != null)
			{
				newRadiated = true;
				newDeadzoneInfo = deadzoneVolume;
			}

			if (newSafe != isSafe)
			{
				_isSafe = newSafe;

				onSafetyUpdated?.Invoke(isSafe);
			}

			ActiveDeadzone = newDeadzoneInfo;
			if (newRadiated != isRadiated)
			{
				_isRadiated = newRadiated;

				onRadiationUpdated?.Invoke(isRadiated);
			}

			if (newPurchaseNode != purchaseNode)
			{
				_purchaseNode = newPurchaseNode;

				onPurchaseUpdated?.Invoke(purchaseNode);
			}

			player.inventory.closeDistantStorage();
		}

		internal void InitializePlayer()
		{
			itemGravityMultiplier = 1;
			pluginGravityMultiplier = 1;
			pluginSpeedMultiplier = 1;

			_region_x = 255;
			_region_y = 255;
			_bound = 255;
			_nav = 255;

			if (channel.IsLocalPlayer || Provider.isServer)
			{
				_loadedRegions = new LoadedRegion[Regions.WORLD_SIZE, Regions.WORLD_SIZE];
				for (byte x = 0; x < Regions.WORLD_SIZE; x++)
				{
					for (byte y = 0; y < Regions.WORLD_SIZE; y++)
					{
						loadedRegions[x, y] = new LoadedRegion();
					}
				}

				_loadedBounds = new LoadedBound[LevelNavigation.bounds.Count];
				for (byte index = 0; index < LevelNavigation.bounds.Count; index++)
				{
					loadedBounds[index] = new LoadedBound();
				}
			}

			warp_x = 1;
			warp_y = 1;

			if (Provider.isServer || channel.IsLocalPlayer) // if(channel.isOwner)//
			{
				controller = GetComponent<CharacterController>();

				// Overlap recovery tries to force character's capsule out of
				// rigidbodies that it overlaps which might be fine in singleplayer,
				// but is inevitable in multiplayer. Often players abused overlaps
				// to teleport through walls and the like, so it's important to disable.
				//
				// Sadly there are issues when walking toward slopes that bend inward,
				// so CheckedMove performs a capsule sweep from start to end.
				// To test if it works in the future walk toward the inside of a barracks building in PEI base.
				//
				// 2021-09-16: tried enabling again because CheckedMove can return false positives, for example when
				// substep moves around a corner. Unfortunately vehicles falling on players heads or driven into a
				// player near a wall were abused in multiplayer to teleport through walls. In the future we might be
				// able to work around this by using fully authoritative vehicle physics.
				//
				// 2026-05-04: there is speculation that overlap and sweep tests necessary when overlap recovery is
				// *disabled* were very expensive on maps with lots of colliders, so we added an option to enable it.
				controller.enableOverlapRecovery = CharacterControllerExtension.EnableOverlapRecovery;

				source = new SourceMovement(this);
			}

			if (Provider.isServer)
			{
				//capsule = gameObject.AddComponent<CapsuleCollider>();
				//capsule.isTrigger = true;
				//capsule.center = new Vector3(0, 1, 0);
				//capsule.radius = controller.radius;
				//capsule.height = controller.height;
				//capsule.enabled = false; //!channel.isOwner;

				player.life.onVisionUpdated += onVisionUpdated;
			}
			else
			{
				nsb = new NetworkSnapshotBuffer<PitchYawSnapshotInfo>(Provider.UPDATE_TIME, Provider.UPDATE_DELAY);
			}

			applySize();

			if (Dedicator.IsDedicatedServer)
			{
				gameObject.AddComponent<Rigidbody>();
				GetComponent<Rigidbody>().useGravity = false;
				GetComponent<Rigidbody>().isKinematic = true;
			}

			updateMovement();

			updates = new List<PlayerStateUpdate>();
			canAddSimulationResultsToUpdates = true;

			lastFootstep = Time.time;
			lastFootstepPosition = transform.position;
		}

		private void OnControllerColliderHit(ControllerColliderHit hit)
		{
			mostRecentControllerColliderHit = hit;
		}

		private void OnDrawGizmos()
		{
			if (nsb == null)
			{
				return;
			}

			for (int index = 0; index < nsb.snapshots.Length; index++)
			{
				if (nsb.snapshots[index].timestamp <= 0.01f)
				{
					return;
				}

				PitchYawSnapshotInfo info = nsb.snapshots[index].info;
				Gizmos.DrawLine(info.pos, info.pos + (Vector3.up * 2));
			}
		}

		private void OnDestroy()
		{
			updates = null;
		}

		/// <summary>
		/// In the future this can probably replace checkGround for locally simulated character?
		/// (Unturned only started using OnControllerColliderHit on 2023-01-31)
		///
		/// 2023-02-28: be careful with .gameObject property because it returns .collider.gameObject
		/// which can cause a null reference exception. (public issue #3726)
		/// </summary>
		private ControllerColliderHit mostRecentControllerColliderHit;
	}
}
