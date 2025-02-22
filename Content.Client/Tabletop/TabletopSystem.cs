using Content.Client.Tabletop.Components;
using Content.Client.Tabletop.UI;
using Content.Client.Viewport;
using Content.Shared.Tabletop;
using Content.Shared.Tabletop.Events;
using JetBrains.Annotations;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using static Robust.Shared.Input.Binding.PointerInputCmdHandler;
using DrawDepth = Content.Shared.DrawDepth.DrawDepth;

namespace Content.Client.Tabletop
{
    [UsedImplicitly]
    public class TabletopSystem : SharedTabletopSystem
    {
        [Dependency] private readonly IInputManager _inputManager = default!;
        [Dependency] private readonly IUserInterfaceManager _uiManger = default!;
        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IGameTiming _gameTiming = default!;

        // Time in seconds to wait until sending the location of a dragged entity to the server again
        private const float Delay = 1f / 10; // 10 Hz

        private float _timePassed; // Time passed since last update sent to the server.
        private EntityUid? _draggedEntity; // Entity being dragged
        private ScalingViewport? _viewport; // Viewport currently being used
        private SS14Window? _window; // Current open tabletop window (only allow one at a time)
        private EntityUid? _table; // The table entity of the currently open game session

        public override void Initialize()
        {
            CommandBinds.Builder
                        .Bind(EngineKeyFunctions.Use, new PointerInputCmdHandler(OnUse, false))
                        .Register<TabletopSystem>();

            SubscribeNetworkEvent<TabletopPlayEvent>(OnTabletopPlay);
            SubscribeLocalEvent<TabletopDraggableComponent, ComponentHandleState>(HandleComponentState);
        }

        public override void Update(float frameTime)
        {
            // don't send network messages when doing prediction.
            if (!_gameTiming.IsFirstTimePredicted)
                return;

            // If there is no player entity, return
            if (_playerManager.LocalPlayer is not {ControlledEntity: { } playerEntity}) return;

            if (StunnedOrNoHands(playerEntity))
            {
                StopDragging();
            }

            if (!CanSeeTable(playerEntity, _table))
            {
                StopDragging();
                _window?.Close();
                return;
            }

            // If no entity is being dragged or no viewport is clicked, return
            if (_draggedEntity == null || _viewport == null) return;

            // Make sure the dragged entity has a draggable component
            if (!EntityManager.TryGetComponent<TabletopDraggableComponent>(_draggedEntity.Value, out var draggableComponent)) return;

            // If the dragged entity has another dragging player, drop the item
            // This should happen if the local player is dragging an item, and another player grabs it out of their hand
            if (draggableComponent.DraggingPlayer != null &&
                draggableComponent.DraggingPlayer != _playerManager.LocalPlayer?.Session.UserId)
            {
                StopDragging(false);
                return;
            }

            // Map mouse position to EntityCoordinates
            var coords = _viewport.ScreenToMap(_inputManager.MouseScreenPosition.Position);

            // Clamp coordinates to viewport
            var clampedCoords = ClampPositionToViewport(coords, _viewport);
            if (clampedCoords.Equals(MapCoordinates.Nullspace)) return;

            // Move the entity locally every update
            EntityManager.GetComponent<TransformComponent>(_draggedEntity.Value).WorldPosition = clampedCoords.Position;

            // Increment total time passed
            _timePassed += frameTime;

            // Only send new position to server when Delay is reached
            if (_timePassed >= Delay && _table != null)
            {
                RaiseNetworkEvent(new TabletopMoveEvent(_draggedEntity.Value, clampedCoords, _table.Value));
                _timePassed -= Delay;
            }
        }

        #region Event handlers

        /// <summary>
        /// Runs when the player presses the "Play Game" verb on a tabletop game.
        /// Opens a viewport where they can then play the game.
        /// </summary>
        private void OnTabletopPlay(TabletopPlayEvent msg)
        {
            // Close the currently opened window, if it exists
            _window?.Close();

            _table = msg.TableUid;

            // Get the camera entity that the server has created for us
            var camera = msg.CameraUid;

            if (!EntityManager.TryGetComponent<EyeComponent>(camera, out var eyeComponent))
            {
                // If there is no eye, print error and do not open any window
                Logger.Error("Camera entity does not have eye component!");
                return;
            }

            // Create a window to contain the viewport
            _window = new TabletopWindow(eyeComponent.Eye, (msg.Size.X, msg.Size.Y))
            {
                MinWidth = 500,
                MinHeight = 436,
                Title = msg.Title
            };

            _window.OnClose += OnWindowClose;
        }

        private void HandleComponentState(EntityUid uid, TabletopDraggableComponent component, ref ComponentHandleState args)
        {
            if (args.Current is not TabletopDraggableComponentState state) return;

            component.DraggingPlayer = state.DraggingPlayer;
        }

        private void OnWindowClose()
        {
            if (_table != null)
            {
                RaiseNetworkEvent(new TabletopStopPlayingEvent(_table.Value));
            }

            StopDragging();
            _window = null;
        }

        private bool OnUse(in PointerInputCmdArgs args)
        {
            return args.State switch
            {
                BoundKeyState.Down => OnMouseDown(args),
                BoundKeyState.Up => OnMouseUp(args),
                _ => false
            };
        }

        private bool OnMouseDown(in PointerInputCmdArgs args)
        {
            // Return if no player entity
            if (_playerManager.LocalPlayer is not {ControlledEntity: { } playerEntity})
                return false;

            // Return if can not see table or stunned/no hands
            if (!CanSeeTable(playerEntity, _table) || StunnedOrNoHands(playerEntity))
            {
                return false;
            }

            var draggedEntity = args.EntityUid;

            // Set the entity being dragged and the viewport under the mouse
            if (!EntityManager.EntityExists(draggedEntity))
            {
                return false;
            }

            // Make sure that entity can be dragged
            if (!EntityManager.HasComponent<TabletopDraggableComponent>(draggedEntity))
            {
                return false;
            }

            // Try to get the viewport under the cursor
            if (_uiManger.MouseGetControl(args.ScreenCoordinates) as ScalingViewport is not { } viewport)
            {
                return false;
            }

            StartDragging(draggedEntity, viewport);
            return true;
        }

        private bool OnMouseUp(in PointerInputCmdArgs args)
        {
            StopDragging();
            return false;
        }

        #endregion

        #region Utility

        /// <summary>
        /// Start dragging an entity in a specific viewport.
        /// </summary>
        /// <param name="draggedEntity">The entity that we start dragging.</param>
        /// <param name="viewport">The viewport in which we are dragging.</param>
        private void StartDragging(EntityUid draggedEntity, ScalingViewport viewport)
        {
            RaiseNetworkEvent(new TabletopDraggingPlayerChangedEvent(draggedEntity, _playerManager.LocalPlayer?.UserId));

            if (EntityManager.TryGetComponent<AppearanceComponent>(draggedEntity, out var appearance))
            {
                appearance.SetData(TabletopItemVisuals.Scale, new Vector2(1.25f, 1.25f));
                appearance.SetData(TabletopItemVisuals.DrawDepth, (int) DrawDepth.Items + 1);
            }

            _draggedEntity = draggedEntity;
            _viewport = viewport;
        }

        /// <summary>
        /// Stop dragging the entity.
        /// </summary>
        /// <param name="broadcast">Whether to tell other clients that we stopped dragging.</param>
        private void StopDragging(bool broadcast = true)
        {
            // Set the dragging player on the component to noone
            if (broadcast && _draggedEntity != null && EntityManager.HasComponent<TabletopDraggableComponent>(_draggedEntity.Value))
            {
                RaiseNetworkEvent(new TabletopDraggingPlayerChangedEvent(_draggedEntity.Value, null));
            }

            _draggedEntity = null;
            _viewport = null;
        }

        /// <summary>
        /// Clamps coordinates within a viewport. ONLY WORKS FOR 90 DEGREE ROTATIONS!
        /// </summary>
        /// <param name="coordinates">The coordinates to be clamped.</param>
        /// <param name="viewport">The viewport to clamp the coordinates to.</param>
        /// <returns>Coordinates clamped to the viewport.</returns>
        private static MapCoordinates ClampPositionToViewport(MapCoordinates coordinates, ScalingViewport viewport)
        {
            if (coordinates == MapCoordinates.Nullspace) return MapCoordinates.Nullspace;

            var eye = viewport.Eye;
            if (eye == null) return MapCoordinates.Nullspace;

            var size = (Vector2) viewport.ViewportSize / EyeManager.PixelsPerMeter; // Convert to tiles instead of pixels
            var eyePosition = eye.Position.Position;
            var eyeRotation = eye.Rotation;
            var eyeScale = eye.Scale;

            var min = (eyePosition - size / 2) / eyeScale;
            var max = (eyePosition + size / 2) / eyeScale;

            // If 90/270 degrees rotated, flip X and Y
            if (MathHelper.CloseToPercent(eyeRotation.Degrees % 180d, 90d) || MathHelper.CloseToPercent(eyeRotation.Degrees % 180d, -90d))
            {
                (min.Y, min.X) = (min.X, min.Y);
                (max.Y, max.X) = (max.X, max.Y);
            }

            var clampedPosition = Vector2.Clamp(coordinates.Position, min, max);

            // Use the eye's map ID, we don't want anything moving to a different map!
            return new MapCoordinates(clampedPosition, eye.Position.MapId);
        }

        #endregion
    }
}
