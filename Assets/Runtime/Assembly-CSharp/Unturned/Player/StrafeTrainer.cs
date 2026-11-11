////////////////////////////////////////////////////////////////////////////////////////
// This file is part of the U3 SDK: https://github.com/smartlydressedgames/u3-sdk/    //
// Please refer to the included LICENSE.txt for copyright notice and license details. //
////////////////////////////////////////////////////////////////////////////////////////
using UnityEngine;

namespace SDG.Unturned
{
	/// <summary>
	/// Measures how closely the local player's mouse turn rate matches the ideal strafe turn rate.
	///
	/// Each tick, the ideal turn is how far the velocity rotates when accelerating at the optimal angle to it
	/// (the angle that gains the most speed). Turning the view by exactly that much keeps wishdir at the optimal
	/// angle every tick. A throttle of 1 is perfect, below 1 is too little mouse movement, above 1 is too much.
	///
	/// Only fed by client prediction (never by resimulation or the server).
	/// </summary>
	internal class StrafeTrainer
	{
		/// <summary>
		/// Number of 100 Hz ticks averaged. Smooths out frames where several ticks share one mouse sample.
		/// </summary>
		public const int WINDOW_TICKS = 8;

		/// <summary>
		/// The trainer runs whenever horizontal speed is at least this, on the ground (prestrafe) or in the air.
		/// </summary>
		public const float MIN_SPEED = 260.0f;

		private struct Sample
		{
			public bool isValid;
			public bool isRightStrafe;
			public float idealTurnDegrees;
			public float turnDegrees;
		}

		private readonly Sample[] samples = new Sample[WINDOW_TICKS];
		private int nextSampleIndex;
		private float previousYaw;
		private bool hasPreviousYaw;

		public void Reset()
		{
			for (int index = 0; index < samples.Length; ++index)
			{
				samples[index] = default;
			}

			hasPreviousYaw = false;
		}

		/// <summary>
		/// Record one predicted tick. yaw is the command's view yaw in degrees.
		/// </summary>
		public void AddTick(SourceMovement source, float yaw)
		{
			float turnDegrees = hasPreviousYaw ? Mathf.DeltaAngle(previousYaw, yaw) : 0.0f;
			previousYaw = yaw;
			hasPreviousYaw = true;

			Sample sample = default;
			if (source.strafeSampleValid && TryGetIdealTurn(source, out bool isRightStrafe, out float idealTurnDegrees))
			{
				sample.isValid = true;
				sample.isRightStrafe = isRightStrafe;
				sample.idealTurnDegrees = idealTurnDegrees;
				// Unity yaw increases when turning right.
				sample.turnDegrees = isRightStrafe ? turnDegrees : -turnDegrees;
			}

			samples[nextSampleIndex] = sample;
			nextSampleIndex = (nextSampleIndex + 1) % samples.Length;
		}

		/// <summary>
		/// Average throttle over the window for the most recent strafe direction.
		/// Returns false if there were no strafing ticks at or above MIN_SPEED in the window.
		/// </summary>
		public bool TryGetThrottle(out float throttle, out bool isRightStrafe)
		{
			throttle = 0.0f;
			isRightStrafe = false;

			bool hasNewest = false;
			for (int age = 0; age < samples.Length; ++age)
			{
				int index = (nextSampleIndex - 1 - age + (samples.Length * 2)) % samples.Length;
				if (samples[index].isValid)
				{
					isRightStrafe = samples[index].isRightStrafe;
					hasNewest = true;
					break;
				}
			}

			if (!hasNewest)
				return false;

			float idealSum = 0.0f;
			float turnSum = 0.0f;
			foreach (Sample sample in samples)
			{
				if (sample.isValid && sample.isRightStrafe == isRightStrafe)
				{
					idealSum += sample.idealTurnDegrees;
					turnSum += sample.turnDegrees;
				}
			}

			if (idealSum <= 0.0f)
				return false;

			throttle = Mathf.Max(0.0f, turnSum / idealSum);
			return true;
		}

		private static bool TryGetIdealTurn(SourceMovement source, out bool isRightStrafe, out float idealTurnDegrees)
		{
			isRightStrafe = false;
			idealTurnDegrees = 0.0f;

			Vector3 velocity = source.strafeSampleVelocity;
			float speed = velocity.magnitude;
			if (speed < (MIN_SPEED - 0.5f) * SourceMovement.METERS_PER_UNIT)
				return false;

			// Which side of the velocity the player is pushing toward. (Positive is right in Unity.)
			float sideSine = Vector3.Cross(velocity, source.strafeSampleWishdir).y / speed;
			if (Mathf.Abs(sideSine) < 0.01f)
				return false; // Not strafing, e.g. holding forward in the direction of travel.

			isRightStrafe = sideSine > 0.0f;

			// Optimal angle between velocity and wishdir: cos = (cap - accel) / speed, at most 90 degrees.
			float speedCap = source.strafeSampleSpeedCap;
			float accelSpeed = source.strafeSampleAccelSpeed;
			float optimalCos = Mathf.Clamp((speedCap - accelSpeed) / speed, 0.0f, 1.0f);
			float optimalSin = Mathf.Sqrt(1.0f - (optimalCos * optimalCos));

			// Speed gained along wishdir at that angle, and how far it rotates the velocity.
			float gain = Mathf.Clamp(speedCap - (speed * optimalCos), 0.0f, accelSpeed);
			idealTurnDegrees = Mathf.Atan2(gain * optimalSin, speed + (gain * optimalCos)) * Mathf.Rad2Deg;
			return idealTurnDegrees > 1e-4f;
		}
	}
}
