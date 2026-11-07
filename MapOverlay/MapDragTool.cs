using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.InputSystem;

namespace MapOverlay
{
    // Game tool that moves the overlay with the mouse. Drag slides the map so the grabbed point
    // stays under the cursor; Shift + drag turns it around the point the camera looks at.
    // While a drag is in progress the tiles move as one piece; the new placement is saved on
    // release. Right click or Escape returns to the default tool.
    public partial class MapDragTool : ToolBaseSystem
    {
        private bool m_Dragging;
        private bool m_Rotating;
        private float3 m_Start;
        private float3 m_Pivot;

        public override string toolID => "MapOverlay.Drag";

        internal bool IsActive => m_ToolSystem != null && m_ToolSystem.activeTool == this;

        public override PrefabBase GetPrefab() => null;

        public override bool TrySetPrefab(PrefabBase prefab) => false;

        internal void SetActive(bool active)
        {
            if (active == IsActive) return;
            m_ToolSystem.activeTool = active ? this : m_DefaultToolSystem;
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain;
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
            var setting = Mod.Setting;
            if (setting != null && !setting.Visible)
            {
                setting.Visible = true;
                setting.ApplyAndSave();
            }
        }

        protected override void OnStopRunning()
        {
            EndDrag(false);
            applyAction.shouldBeEnabled = false;
            cancelAction.shouldBeEnabled = false;
            base.OnStopRunning();
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var controller = Mod.Controller;
            if (controller == null) return inputDeps;

            // Hiding the map from the panel or the Options menu ends the tool too.
            if (Mod.Setting == null || !Mod.Setting.Visible)
            {
                SetActive(false);
                return inputDeps;
            }

            if (cancelAction.WasPressedThisFrame())
            {
                // The first cancel drops a drag in progress, the next one leaves the tool.
                if (m_Dragging) EndDrag(false);
                else SetActive(false);
                return inputDeps;
            }

            bool hit = GetRaycastResult(out ControlPoint point);

            if (!m_Dragging && applyAction.WasPressedThisFrame() && hit)
            {
                m_Dragging = true;
                m_Rotating = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
                m_Start = point.m_HitPosition;
                if (!m_Rotating || !controller.TryGetCameraPivot(out m_Pivot)) m_Rotating = false;
                controller.BeginPreview();
            }

            if (!m_Dragging) return inputDeps;

            if (hit)
            {
                float3 now = point.m_HitPosition;
                if (m_Rotating)
                    controller.SetPreview(float3.zero, Bearing(now - m_Pivot) - Bearing(m_Start - m_Pivot), m_Pivot);
                else
                    controller.SetPreview(new float3(now.x - m_Start.x, 0f, now.z - m_Start.z), 0f, m_Pivot);
            }

            if (applyAction.WasReleasedThisFrame() || !applyAction.IsPressed()) EndDrag(true);
            return inputDeps;
        }

        private void EndDrag(bool commit)
        {
            if (!m_Dragging) return;
            m_Dragging = false;
            if (commit) Mod.Controller?.CommitPreview();
            else Mod.Controller?.CancelPreview();
        }

        // Clockwise angle from north (+Z), in degrees: the sense of Setting.Rotation.
        private static float Bearing(float3 v) => math.degrees(math.atan2(v.x, v.z));
    }
}
