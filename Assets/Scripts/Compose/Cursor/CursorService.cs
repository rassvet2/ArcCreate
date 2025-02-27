using System;
using ArcCreate.Compose.Components;
using ArcCreate.Compose.Navigation;
using ArcCreate.Utility.Extension;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ArcCreate.Compose.Cursor
{
    [EditorScope("Cursor")]
    public class CursorService : MonoBehaviour, ICursorService
    {
        [SerializeField] private MeshCollider laneCollider;
        [SerializeField] private MeshCollider verticalCollider;
        [SerializeField] private LineRenderer cursorLaneTiming;
        [SerializeField] private LineRenderer cursorLaneX;
        [SerializeField] private LineRenderer cursorVerticalX;
        [SerializeField] private LineRenderer cursorVerticalY;
        [SerializeField] private Camera editorCamera;
        [SerializeField] private RectTransform gameplayViewport;
        private bool isCursorAboveViewport;
        private bool isLaneCursorEnabled;
        private bool isHittingLane;
        private bool isHittingVertical;
        private int selectingTiming;
        private int selectingLane;
        private Vector2 selectingVerticalPoint;
        private Vector3 cursorWorldPosition;
        private int showVerticalAtTiming;

        public bool EnableLaneCursor
        {
            get => isLaneCursorEnabled;
            set => isLaneCursorEnabled = value;
        }

        public int CursorTiming => selectingTiming;

        public int CursorLane => selectingLane;

        public bool IsHittingLane => isHittingLane;

        public bool IsCursorAboveViewport => isCursorAboveViewport;

        public Vector3 CursorWorldPosition => cursorWorldPosition;

        public async UniTask<(bool wasSuccessful, int timing)> RequestTimingSelection(
            SubAction confirm,
            SubAction cancel,
            Action<int> update = null,
            Func<int, bool> constraint = null)
        {
            isLaneCursorEnabled = true;
            var result = await RequestValue(
                confirm: confirm,
                cancel: cancel,
                selector: () => selectingTiming,
                update: update,
                constraint: constraint);

            isLaneCursorEnabled = false;
            return result;
        }

        public async UniTask<(bool wasSuccessful, int lane)> RequestLaneSelection(
            SubAction confirm,
            SubAction cancel,
            Action<int> update = null,
            Func<int, bool> constraint = null)
        {
            isLaneCursorEnabled = true;
            var result = await RequestValue(
                confirm: confirm,
                cancel: cancel,
                selector: () => selectingLane,
                update: update,
                constraint: (lane) => (constraint?.Invoke(lane) ?? true) && lane >= Gameplay.Values.LaneFrom && lane <= Gameplay.Values.LaneTo);

            isLaneCursorEnabled = false;
            return result;
        }

        public async UniTask<(bool wasSuccessful, Vector2 point)> RequestVerticalSelection(
            SubAction confirm,
            SubAction cancel,
            int showVerticalAtTiming,
            Action<Vector2> update = null,
            Func<Vector2, bool> constraint = null)
        {
            this.showVerticalAtTiming = showVerticalAtTiming;
            verticalCollider.gameObject.SetActive(true);
            var result = await RequestValue(
                confirm: confirm,
                cancel: cancel,
                selector: () => selectingVerticalPoint,
                update: update,
                constraint: constraint);

            verticalCollider.gameObject.SetActive(false);
            return result;
        }

#if UNITY_EDITOR
        [EditorAction("Test", false, "tv")]
        [SubAction("Confirm", false, "<cr>", "<mouse1>")]
        [SubAction("Cancel", false, "<esc>")]
        [WhitelistScopes(typeof(CursorService), typeof(Grid.GridService))]
        public async UniTask TestCursor(EditorAction action)
        {
            SubAction confirm = action.GetSubAction("Confirm");
            SubAction cancel = action.GetSubAction("Cancel");
            (bool timingSuccess, int timing) = await RequestTimingSelection(confirm, cancel);
            if (!timingSuccess)
            {
                return;
            }

            (bool pointSuccess, Vector2 point) = await RequestVerticalSelection(confirm, cancel, timing);
            if (pointSuccess)
            {
                print(point);
            }
        }
#endif

        public void ForceUpdateLaneCursor()
        {
            Camera gameplayCamera = Services.Gameplay.Camera.GameplayCamera;
            Vector2 mousePosition = Input.mousePosition;
            Ray ray = gameplayCamera.ScreenPointToRay(mousePosition);
            bool isCursorHoveringOnTrack = laneCollider.Raycast(ray, out RaycastHit laneHit, 120);
            isHittingLane = isLaneCursorEnabled && isCursorHoveringOnTrack;
            if (isCursorHoveringOnTrack)
            {
                AlignLaneCursor(laneHit);
            }
        }

        private void Update()
        {
            if (Services.Gameplay?.IsLoaded ?? false)
            {
                Camera gameplayCamera = Services.Gameplay.Camera.GameplayCamera;
                Vector2 mousePosition = Input.mousePosition;
                isCursorAboveViewport = !Dialog.IsAnyOpen && RectTransformUtility.RectangleContainsScreenPoint(gameplayViewport, mousePosition, editorCamera);
                if (!isCursorAboveViewport)
                {
                    return;
                }

                Ray ray = gameplayCamera.ScreenPointToRay(mousePosition);

                bool isVerticalActive = verticalCollider.gameObject.activeInHierarchy;
                bool isCursorHoveringOnTrack = laneCollider.Raycast(ray, out RaycastHit laneHit, 120);
                isHittingVertical = verticalCollider.Raycast(ray, out RaycastHit verticalHit, 120);
                isHittingLane = isLaneCursorEnabled && isCursorHoveringOnTrack;

                if (isCursorHoveringOnTrack)
                {
                    CheckScroll();
                }

                if (!isHittingVertical && isHittingLane)
                {
                    AlignLaneCursor(laneHit);
                }
                else
                {
                    DisableLaneCursor();
                }

                if (isHittingVertical)
                {
                    AlignVerticalCursor(verticalHit);
                }
                else
                {
                    DisableVerticalCursor();
                }

                if (verticalCollider.gameObject.activeInHierarchy)
                {
                    UpdateVerticalZPosition();
                }
            }
        }

        private void CheckScroll()
        {
            float scrollY = Input.mouseScrollDelta.y;
            if (scrollY == 0 || Mathf.Abs(scrollY) < Settings.TrackScrollThreshold.Value)
            {
                return;
            }

            scrollY *= Settings.ScrollSensitivityVertical.Value;

            int timing = Services.Gameplay.Audio.ChartTiming;
            int snap = scrollY > 0 ? Services.Grid.MoveTimingForward(timing) : Services.Grid.MoveTimingBackward(timing);
            snap = Mathf.Clamp(snap, timing - Settings.TrackScrollMaxMovement.Value, timing + Settings.TrackScrollMaxMovement.Value);

            Services.Gameplay.Audio.ChartTiming = snap;
        }

        private void AlignLaneCursor(RaycastHit hit)
        {
            cursorLaneTiming.gameObject.SetActive(true);
            cursorLaneX.gameObject.SetActive(true);

            var tg = Services.Gameplay.Chart.GetTimingGroup(Values.EditingTimingGroup.Value);
            int timing = tg.GetTimingFromZPosition(hit.point.z);
            selectingTiming = Services.Grid.SnapTimingToGridIfGridIsEnabled(timing);
            double fp = tg.GetFloorPositionFromCurrent(selectingTiming);
            float z = Gameplay.ArcFormula.FloorPositionToZ(fp);

            cursorLaneTiming.DrawLine(
                from: new Vector3(Values.LaneFromX, 0, z),
                to: new Vector3(Values.LaneToX, 0, z));

            cursorLaneX.DrawLine(
                from: new Vector3(hit.point.x, 0, Gameplay.Values.TrackLengthBackward),
                to: new Vector3(hit.point.x, 0, -Gameplay.Values.TrackLengthForward));

            selectingLane = Gameplay.ArcFormula.WorldXToLane(hit.point.x);
            cursorWorldPosition.z = z;
            cursorWorldPosition.x = hit.point.x;
        }

        private void DisableLaneCursor()
        {
            cursorLaneTiming.gameObject.SetActive(false);
            cursorLaneX.gameObject.SetActive(false);
        }

        private void AlignVerticalCursor(RaycastHit hit)
        {
            cursorVerticalX.gameObject.SetActive(true);
            cursorVerticalY.gameObject.SetActive(true);

            Vector2 snapped = Services.Grid.SnapPointToGridIfEnabled(hit.point);
            float verticalScale = verticalCollider.transform.localScale.y;

            selectingVerticalPoint = new Vector2(
                Gameplay.ArcFormula.WorldXToArc(snapped.x),
                Gameplay.ArcFormula.WorldYToArc(snapped.y));

            selectingVerticalPoint.x = Mathf.Round(selectingVerticalPoint.x * 1000) / 1000;
            selectingVerticalPoint.y = Mathf.Round(selectingVerticalPoint.y * 1000) / 1000;

            if (verticalScale > Mathf.Epsilon)
            {
                snapped.y /= verticalScale;
            }

            (float fromX, float fromY, float toX, float toY) = Services.Grid.GetVerticalGridBound();
            cursorVerticalX.DrawLine(
                from: new Vector3(snapped.x, fromY, 0),
                to: new Vector3(snapped.x, toY, 0));

            cursorVerticalY.DrawLine(
                from: new Vector3(fromX, snapped.y, 0),
                to: new Vector3(toX, snapped.y, 0));
            cursorWorldPosition.x = snapped.x;

            cursorWorldPosition.y = snapped.y;
            cursorWorldPosition.z = hit.point.z;
        }

        private void DisableVerticalCursor()
        {
            cursorVerticalX.gameObject.SetActive(false);
            cursorVerticalY.gameObject.SetActive(false);
        }

        private void UpdateVerticalZPosition()
        {
            var tg = Services.Gameplay.Chart.GetTimingGroup(Values.EditingTimingGroup.Value);
            double fp = tg.GetFloorPositionFromCurrent(showVerticalAtTiming);
            float z = Gameplay.ArcFormula.FloorPositionToZ(fp);

            Vector3 pos = verticalCollider.transform.localPosition;
            pos.z = z;
            verticalCollider.transform.localPosition = pos;
        }

        // Amazing
        private async UniTask<(bool wasSuccessful, T value)> RequestValue<T>(
            SubAction confirm,
            SubAction cancel,
            Func<T> selector,
            Action<T> update = null,
            Func<T, bool> constraint = null)
        {
            T result = default;
            bool resultSet = false;

            bool wasSuccessful = false;
            while (true)
            {
                T selecting = selector.Invoke();
                bool valid = constraint?.Invoke(selecting) ?? true;
                if (valid)
                {
                    result = selecting;
                    resultSet = true;
                    update?.Invoke(result);
                }

                if (confirm.WasExecuted)
                {
                    wasSuccessful = resultSet;
                    break;
                }

                if (cancel.WasExecuted)
                {
                    wasSuccessful = false;
                    break;
                }

                await UniTask.NextFrame();
            }

            return (wasSuccessful, result);
        }
    }
}