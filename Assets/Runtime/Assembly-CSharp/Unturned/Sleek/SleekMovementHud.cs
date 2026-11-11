////////////////////////////////////////////////////////////////////////////////////////
// This file is part of the U3 SDK: https://github.com/smartlydressedgames/u3-sdk/    //
// Please refer to the included LICENSE.txt for copyright notice and license details. //
////////////////////////////////////////////////////////////////////////////////////////
using UnityEngine;

namespace SDG.Unturned
{
	/// <summary>
	/// Bhop HUD: "half width throttle" strafe trainer bar with a horizontal speedometer underneath, placed
	/// halfway between the crosshair and the bottom of the screen.
	///
	/// Trainer bar: strafing right fills from the left edge toward the center tick, strafing left fills from the
	/// right edge. Reaching the center tick means turning at exactly the ideal rate, short of it is too little
	/// mouse movement, past it is too much.
	/// </summary>
	public class SleekMovementHud : SleekWrapper
	{
		private const int BAR_WIDTH = 300;
		private const int BAR_HEIGHT = 14;
		private const int TICK_WIDTH = 2;
		private const int SPEED_LABEL_WIDTH = 200;
		private const int SPEED_LABEL_HEIGHT = 30;
		private const float VERTICAL_POSITION = 0.75f; // Halfway between crosshair (0.5) and bottom (1.0).

		/// <summary>
		/// Throttle below which the fill is yellow rather than green.
		/// </summary>
		private const float GOOD_THROTTLE = 0.5f;

		private const float SPEED_COLORIZE_INTERVAL = 0.1f;
		private const float SPEED_COLORIZE_DEADZONE = 2.0f; // u/s per second, like Momentum.

		private static readonly Color32 TRACK_COLOR = new Color32(42, 48, 54, 255);
		private static readonly Color32 CENTER_TICK_COLOR = new Color32(18, 21, 24, 255);
		private static readonly Color32 OVER_COLOR = new Color32(86, 200, 245, 255);
		private static readonly Color32 GOOD_COLOR = new Color32(76, 196, 96, 255);
		private static readonly Color32 BAD_COLOR = new Color32(245, 212, 46, 255);
		private static readonly Color32 VERY_BAD_COLOR = new Color32(236, 230, 190, 255);

		private static readonly Color32 SPEED_FLAT_COLOR = new Color32(255, 255, 255, 255);
		private static readonly Color32 SPEED_GAIN_COLOR = new Color32(24, 150, 211, 255);
		private static readonly Color32 SPEED_LOSS_COLOR = new Color32(255, 106, 106, 255);

		private static Texture2D circleTexture;

		private readonly ISleekElement trainerFrame;
		private readonly ISleekImage fillOuterCap;
		private readonly ISleekImage fillBody;
		private readonly ISleekImage fillFarCap;
		private readonly ISleekLabel speedLabel;

		private float lastSpeedColorizeTime;
		private float lastSpeedColorizeValue;
		private Color32 speedColor = SPEED_FLAT_COLOR;

		public SleekMovementHud()
		{
			Texture2D circle = GetCircleTexture();

			trainerFrame = Glazier.Get().CreateFrame();
			trainerFrame.PositionScale_X = 0.5f;
			trainerFrame.PositionScale_Y = VERTICAL_POSITION;
			trainerFrame.PositionOffset_X = -BAR_WIDTH / 2;
			trainerFrame.PositionOffset_Y = -BAR_HEIGHT / 2;
			trainerFrame.SizeOffset_X = BAR_WIDTH;
			trainerFrame.SizeOffset_Y = BAR_HEIGHT;
			AddChild(trainerFrame);

			// Track: rounded ends plus a straight middle.
			trainerFrame.AddChild(CreatePiece(circle, 0, BAR_HEIGHT, TRACK_COLOR));
			trainerFrame.AddChild(CreatePiece(circle, BAR_WIDTH - BAR_HEIGHT, BAR_HEIGHT, TRACK_COLOR));
			trainerFrame.AddChild(CreatePiece(GlazierResources.PixelTexture, BAR_HEIGHT / 2, BAR_WIDTH - BAR_HEIGHT, TRACK_COLOR));
			trainerFrame.AddChild(CreatePiece(GlazierResources.PixelTexture, (BAR_WIDTH - TICK_WIDTH) / 2, TICK_WIDTH, CENTER_TICK_COLOR));

			// Fill: rounded at the edge it grows from, square at its leading end.
			fillOuterCap = CreatePiece(circle, 0, BAR_HEIGHT, GOOD_COLOR);
			fillBody = CreatePiece(GlazierResources.PixelTexture, 0, 0, GOOD_COLOR);
			fillFarCap = CreatePiece(circle, 0, BAR_HEIGHT, GOOD_COLOR);
			trainerFrame.AddChild(fillOuterCap);
			trainerFrame.AddChild(fillBody);
			trainerFrame.AddChild(fillFarCap);

			speedLabel = Glazier.Get().CreateLabel();
			speedLabel.PositionScale_X = 0.5f;
			speedLabel.PositionScale_Y = VERTICAL_POSITION;
			speedLabel.PositionOffset_X = -SPEED_LABEL_WIDTH / 2;
			speedLabel.PositionOffset_Y = (BAR_HEIGHT / 2) + 2;
			speedLabel.SizeOffset_X = SPEED_LABEL_WIDTH;
			speedLabel.SizeOffset_Y = SPEED_LABEL_HEIGHT;
			speedLabel.TextAlignment = TextAnchor.UpperCenter;
			speedLabel.FontSize = ESleekFontSize.Medium;
			speedLabel.TextContrastContext = ETextContrastContext.ColorfulBackdrop;
			speedLabel.TextColor = SPEED_FLAT_COLOR;
			AddChild(speedLabel);

			trainerFrame.IsVisible = false;
			speedLabel.IsVisible = false;
		}

		public override void OnUpdate()
		{
			Player player = Player.LocalPlayer;
			bool isOnFoot = player != null
				&& player.life.IsAlive
				&& player.movement.source != null
				&& player.movement.getVehicle() == null
				&& player.stance.stance != EPlayerStance.DRIVING
				&& player.stance.stance != EPlayerStance.SITTING;

			if (!isOnFoot)
			{
				trainerFrame.IsVisible = false;
				speedLabel.IsVisible = false;
				return;
			}

			UpdateSpeedometer(player);
			UpdateTrainer(player);
		}

		private void UpdateSpeedometer(Player player)
		{
			Vector3 velocity = player.movement.velocity;
			float speed = new Vector2(velocity.x, velocity.z).magnitude / SourceMovement.METERS_PER_UNIT;

			// Blue while gaining speed, red while losing it, white otherwise.
			float now = Time.time;
			float elapsed = now - lastSpeedColorizeTime;
			if (elapsed >= SPEED_COLORIZE_INTERVAL)
			{
				float deadzone = SPEED_COLORIZE_DEADZONE * elapsed;
				float diff = speed - lastSpeedColorizeValue;
				if (diff > deadzone)
				{
					speedColor = SPEED_GAIN_COLOR;
				}
				else if (diff < -deadzone)
				{
					speedColor = SPEED_LOSS_COLOR;
				}
				else
				{
					speedColor = SPEED_FLAT_COLOR;
				}

				lastSpeedColorizeTime = now;
				lastSpeedColorizeValue = speed;
			}

			speedLabel.Text = Mathf.RoundToInt(speed).ToString();
			speedLabel.TextColor = speedColor;
			speedLabel.IsVisible = true;
		}

		private void UpdateTrainer(Player player)
		{
			if (!player.movement.strafeTrainer.TryGetThrottle(out float throttle, out bool isRightStrafe))
			{
				trainerFrame.IsVisible = false;
				return;
			}

			trainerFrame.IsVisible = true;

			// A full half (edge to center) is a throttle of 1. Overshooting continues into the other half.
			float fillLength = Mathf.Clamp(throttle * (BAR_WIDTH * 0.5f), 0.0f, BAR_WIDTH);
			if (fillLength < 1.0f)
			{
				fillOuterCap.IsVisible = false;
				fillBody.IsVisible = false;
				fillFarCap.IsVisible = false;
				return;
			}

			Color color = GetThrottleColor(throttle);
			fillOuterCap.TintColor = color;
			fillBody.TintColor = color;
			fillFarCap.TintColor = color;

			const float halfHeight = BAR_HEIGHT * 0.5f;
			float bodyLength = Mathf.Max(0.0f, Mathf.Min(fillLength, BAR_WIDTH - halfHeight) - halfHeight);
			bool reachesFarEnd = fillLength >= BAR_WIDTH - halfHeight;

			fillOuterCap.IsVisible = true;
			fillBody.IsVisible = bodyLength > 0.0f;
			fillBody.SizeOffset_X = bodyLength;
			fillFarCap.IsVisible = reachesFarEnd;

			if (isRightStrafe)
			{
				// Grows from the left edge.
				fillOuterCap.PositionOffset_X = 0;
				fillBody.PositionOffset_X = halfHeight;
				fillFarCap.PositionOffset_X = BAR_WIDTH - BAR_HEIGHT;
			}
			else
			{
				// Grows from the right edge.
				fillOuterCap.PositionOffset_X = BAR_WIDTH - BAR_HEIGHT;
				fillBody.PositionOffset_X = BAR_WIDTH - halfHeight - bodyLength;
				fillFarCap.PositionOffset_X = 0;
			}
		}

		private static Color GetThrottleColor(float throttle)
		{
			if (throttle > 1.0f)
				return OVER_COLOR;

			if (throttle >= GOOD_THROTTLE)
				return GOOD_COLOR;

			// Fades toward pale for very little mouse movement.
			return Color.Lerp(VERY_BAD_COLOR, BAD_COLOR, Mathf.Clamp01(throttle / 0.3f));
		}

		private static ISleekImage CreatePiece(Texture texture, float x, float width, Color color)
		{
			ISleekImage image = Glazier.Get().CreateImage(texture);
			image.PositionOffset_X = x;
			image.SizeOffset_X = width;
			image.SizeOffset_Y = BAR_HEIGHT;
			image.TintColor = color;
			return image;
		}

		/// <summary>
		/// White anti-aliased circle used for the rounded bar ends.
		/// </summary>
		private static Texture2D GetCircleTexture()
		{
			if (circleTexture != null)
				return circleTexture;

			const int size = 64;
			circleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
			circleTexture.name = "MovementHudCircle";
			circleTexture.hideFlags = HideFlags.HideAndDontSave;
			circleTexture.wrapMode = TextureWrapMode.Clamp;
			circleTexture.filterMode = FilterMode.Bilinear;

			Color32[] pixels = new Color32[size * size];
			float radius = size * 0.5f;
			for (int y = 0; y < size; ++y)
			{
				for (int x = 0; x < size; ++x)
				{
					float dx = (x + 0.5f) - radius;
					float dy = (y + 0.5f) - radius;
					float distance = Mathf.Sqrt((dx * dx) + (dy * dy));
					byte alpha = (byte) Mathf.RoundToInt(Mathf.Clamp01(radius - distance) * 255.0f);
					pixels[(y * size) + x] = new Color32(255, 255, 255, alpha);
				}
			}

			circleTexture.SetPixels32(pixels);
			circleTexture.Apply(false, true);
			return circleTexture;
		}
	}
}
