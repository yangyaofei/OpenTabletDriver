using System;
using System.Threading;
using System.Diagnostics;
using System.Numerics;
using OpenTabletDriver.Desktop.Interop.Input.Keyboard;
using OpenTabletDriver.Native.OSX;
using OpenTabletDriver.Native.OSX.Input;
using OpenTabletDriver.Plugin.Platform.Pointer;

namespace OpenTabletDriver.Desktop.Interop.Input
{
    using static OSX;

    public abstract class MacOSVirtualMouse : IMouseButtonHandler, IMouseScrollHandler, ISynchronousPointer, ITiltHandler, IEraserHandler, IPressureHandler
    {
        private const int DoubleClickMoveTolerance = 8;
        private const int ProximityExpiresDurationInMs = 200;

        // The capabilityMask is essential for Adobe software to recognize tablet pressure sensitivity,
        // a feature specific to Wacom devices.
        private const WacomCapabilityMask TabletCapabilityMask = WacomCapabilityMask.absXBitMask |
                                                                  WacomCapabilityMask.absYBitMask |
                                                                  WacomCapabilityMask.buttonsBitMask |
                                                                  WacomCapabilityMask.pressureBitMask |
                                                                  WacomCapabilityMask.tiltXBitMask |
                                                                  WacomCapabilityMask.tiltYBitMask |
                                                                  WacomCapabilityMask.deviceIdBitMask;

        // https://www.wacomeng.com/mac/Developers%20Guide.htm#vendorPointerType
        private const long VendorPointerType = 0x802; // General Stylus

        // A non-zero deviceId is essential for Adobe software to map tablet events to their
        // corresponding capabilities. Randomly generated and hopefully do not conflict with another driver.
        private const long DeviceId = 5303613955435230461;

        private int _currButtonStates;
        private int _prevButtonStates;
        private float? _pendingX;
        private float? _pendingY;
        private int? _scrollDeltaX;
        private int? _scrollDeltaY;
        private float? _pressure;
        private Vector2? _tilt;
        private bool? _isEraser;
        private CGMouseButton _lastButton;
        private Vector2 _lastMouseDownPosition;
        private bool _mouseMovedSinceLastDown;

        private int _clickState;
        private readonly Stopwatch _stopWatch;
        private readonly Stopwatch _doubleClickStopWatch;
        private readonly IntPtr _eventSource;
        private IntPtr _mouseEvent;
        private readonly double _doubleClickIntervalInMs;
        private readonly MacOSVirtualKeyboard _keyboard;

        public MacOSVirtualMouse()
        {
            _doubleClickIntervalInMs = GetDoubleClickInterval() * 1000;
            _doubleClickStopWatch = new Stopwatch();
            _stopWatch = new Stopwatch();
            _stopWatch.Start();
            _eventSource = CGEventSourceCreate(CGEventSourceStatePrivate);
            _mouseEvent = CGEventCreate(_eventSource);
            _keyboard = DesktopInterop.VirtualKeyboard as MacOSVirtualKeyboard
                        ?? throw new InvalidOperationException("Could not get virtual keyboard");
            _movePoster = new Thread(MovePosterLoop)
            {
                IsBackground = true,
                Name = "otd-move-poster",
                Priority = ThreadPriority.AboveNormal
            };
            _movePoster.Start();
        }

        public void MouseDown(MouseButton button)
        {
            if (!_pendingX.HasValue)
                QueuePendingPositionFromSystem();
            SetButtonState(ref _currButtonStates, ToCGMouseButton(button), true);
        }

        public void MouseUp(MouseButton button)
        {
            if (!_pendingX.HasValue)
                QueuePendingPositionFromSystem();
            SetButtonState(ref _currButtonStates, ToCGMouseButton(button), false);
        }

        public void ScrollVertically(int amount)
        {
            _scrollDeltaX = -amount;
        }

        public void ScrollHorizontally(int amount)
        {
            _scrollDeltaY = -amount;
        }

        public void Flush()
        {
            if (_currButtonStates != _prevButtonStates)
            {
                // keys here just changed, no drag event should be sent
                DrainPendingMove(); // 在途移动先落地，保证点击位置先于按键（顺序）
                ProcessKeyStates(_prevButtonStates, _currButtonStates);
                _prevButtonStates = _currButtonStates;
            }
            else if (DrainPendingPosition() is { } position)
            {
                // can send drag here
                var lastButtonSet = IsButtonSet(_currButtonStates, _lastButton);
                var cgEventType = ToDragCGEventType(_lastButton, lastButtonSet);
                // 事件在读线程构建（状态快照一致），投递交给专职线程：
                // WindowServer 忙碌（全屏动画等）时 CGEventPost 可阻塞数百毫秒，
                // 同步投递会把整个读管线拖停（实测 onReportMax 297ms）。
                var moveEvent = CGEventCreate(_eventSource);
                CGEventSetType(moveEvent, cgEventType);
                SetPendingPosition(moveEvent, position.X, position.Y);
                ApplyTabletValues(moveEvent);
                EnqueueAsyncMove(moveEvent);
            }
            if (_scrollDeltaX.HasValue || _scrollDeltaY.HasValue)
            {
                DrainPendingMove(); // 滚轮前同样先排空移动，保序
                var scrollEventRef = CGEventCreateScrollWheelEvent2(IntPtr.Zero, CGScrollEventUnit.kCGScrollEventUnitPixel, 2, _scrollDeltaX ?? 0, _scrollDeltaY ?? 0, 0);
                CGEventPost(CGEventTapLocation.kCGHIDEventTap, scrollEventRef);
                CFRelease(scrollEventRef);
                _scrollDeltaX = _scrollDeltaY = null;
            }
        }

        // ---- 异步移动投递（latest-wins 合并；按键/滚动同步路径靠 DrainPendingMove 保序）----
        private readonly object _moveGate = new();
        private IntPtr _pendingMoveEvent; // 未投递的最新移动事件（构建线程→投递线程所有权转移）
        private Thread? _movePoster;
        private static int _postStallCount;

        private void EnqueueAsyncMove(IntPtr evt)
        {
            IntPtr stale = IntPtr.Zero;
            lock (_moveGate)
            {
                if (_pendingMoveEvent != IntPtr.Zero)
                    stale = _pendingMoveEvent;
                _pendingMoveEvent = evt;
                Monitor.Pulse(_moveGate);
            }
            if (stale != IntPtr.Zero)
                CFRelease(stale); // 被更新位置覆盖，丢弃（位置流只有最新值有意义）
        }

        private void DrainPendingMove()
        {
            IntPtr evt = IntPtr.Zero;
            lock (_moveGate)
            {
                if (_pendingMoveEvent != IntPtr.Zero)
                {
                    evt = _pendingMoveEvent;
                    _pendingMoveEvent = IntPtr.Zero;
                }
            }
            if (evt != IntPtr.Zero)
            {
                CGEventPost(CGEventTapLocation.kCGHIDEventTap, evt);
                CFRelease(evt);
            }
        }

        private void MovePosterLoop()
        {
            while (true)
            {
                IntPtr evt;
                lock (_moveGate)
                {
                    while (_pendingMoveEvent == IntPtr.Zero)
                        Monitor.Wait(_moveGate, 100);
                    evt = _pendingMoveEvent;
                    _pendingMoveEvent = IntPtr.Zero;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                CGEventPost(CGEventTapLocation.kCGHIDEventTap, evt);
                double ms = sw.Elapsed.TotalMilliseconds;
                CFRelease(evt);
                if (ms > 100)
                {
                    System.Threading.Interlocked.Increment(ref _postStallCount);
                    OpenTabletDriver.Plugin.Log.Write("MacOSVirtualMouse",
                        $"[POSTSTALL] CGEventPost blocked {ms:F0}ms (total stalls={System.Threading.Interlocked.CompareExchange(ref _postStallCount, 0, 0)})",
                        OpenTabletDriver.Plugin.LogLevel.Debug);
                }
            }
        }

        public void Reset()
        {
            // send a key up for all currently held keys
            if (_currButtonStates > 0)
            {
                ProcessKeyStates(_currButtonStates, 0);
                _prevButtonStates = 0;
                _currButtonStates = 0;
            }
        }

        public void SetEraser(bool isEraser)
        {
            if (_isEraser.HasValue && _isEraser.Value == isEraser)
                return;

            _isEraser = isEraser;

            // Immediately post a proximity event to notify applications of the tool change,
            // mirroring how Linux EvdevVirtualTablet sends the tool switch event right away.
            PostProximityEvent();

            // Reset the stopwatch so ApplyTabletValues doesn't redundantly re-send
            // a proximity event on the next frame.
            _stopWatch.Restart();
        }

        public void SetTilt(Vector2 tilt)
        {
            _tilt = tilt;
        }

        protected abstract void SetPendingPosition(IntPtr mouseEvent, float x, float y);
        protected abstract void ResetPendingPosition(IntPtr mouseEvent);

        // binding can be triggered by auxiliary buttons and cursor might be moved by other devices.
        // in such case we fetch the position from system.
        protected abstract void QueuePendingPositionFromSystem();

        protected void QueuePendingPosition(float x, float y)
        {
            _pendingX = x;
            _pendingY = y;
            if (Vector2.Distance(_lastMouseDownPosition, new Vector2(x, y)) > DoubleClickMoveTolerance)
            {
                _mouseMovedSinceLastDown = true;
            }
        }

        public void SetPressure(float percentage)
        {
            _pressure = percentage;
        }

        private void PostProximityEvent()
        {
            var pointerType = _isEraser ?? false
                ? NSPointingDeviceType.Eraser
                : NSPointingDeviceType.Pen;

            var proximityEvent = CGEventCreate(_eventSource);
            CGEventSetType(proximityEvent, CGEventType.kCGEventTabletProximity);
            CGEventSetIntegerValueField(proximityEvent, CGEventField.tabletProximityEventEnterProximity, 1);
            CGEventSetIntegerValueField(proximityEvent, CGEventField.tabletProximityEventPointerType, (long)pointerType);
            CGEventSetIntegerValueField(proximityEvent, CGEventField.tabletProximityEventCapabilityMask, (long)TabletCapabilityMask);
            CGEventSetIntegerValueField(proximityEvent, CGEventField.tabletProximityEventDeviceID, DeviceId);
            CGEventSetIntegerValueField(proximityEvent, CGEventField.tabletProximityEventVendorPointerType, VendorPointerType);

            CGEventPost(CGEventTapLocation.kCGHIDEventTap, proximityEvent);
            CFRelease(proximityEvent);
        }

        private Vector2? DrainPendingPosition()
        {
            if (_pendingX.HasValue && _pendingY.HasValue)
            {
                var vector2 = new Vector2(_pendingX.Value, _pendingY.Value);
                _pendingX = null;
                _pendingY = null;
                return vector2;
            }

            return null;
        }

        private void ProcessKeyStates(int prevButtonStates, int currButtonStates)
        {
            for (int i = 0; i < 5; i++)
            {
                var button = (CGMouseButton)i;
                var currState = IsButtonSet(currButtonStates, button);
                var prevState = IsButtonSet(prevButtonStates, button);

                if (currState != prevState)
                {
                    var doubleClickInvalidated = _mouseMovedSinceLastDown || _doubleClickStopWatch.ElapsedMilliseconds > _doubleClickIntervalInMs;

                    if (currState)
                    {
                        if (_pendingX.HasValue && (_clickState == 0 || doubleClickInvalidated))
                        {
                            _doubleClickStopWatch.Restart();
                            _lastMouseDownPosition = new Vector2(_pendingX.Value, _pendingY!.Value);
                            _mouseMovedSinceLastDown = false;
                            _clickState = 1;
                        }
                        else
                        {
                            _clickState++;
                        }
                    }
                    else
                    {
                        if (doubleClickInvalidated)
                        {
                            _clickState = 0;
                        }
                    }

                    // prepare the mouse event, we reset it here in case
                    // it's a relative event
                    ResetPendingPosition(_mouseEvent);

                    // propagate pending position to mouseEvent
                    var cgEventType = ToNoDragCGEventType(button, currState);
                    CGEventSetType(_mouseEvent, cgEventType);
                    if (DrainPendingPosition() is { } position)
                    {
                        SetPendingPosition(_mouseEvent, position.X, position.Y);
                    }
                    CGEventSetIntegerValueField(_mouseEvent, CGEventField.mouseEventButtonNumber, i);
                    CGEventSetIntegerValueField(_mouseEvent, CGEventField.mouseEventClickState, _clickState); // clickState should be set to 1 (or more) during up, down, and drag events
                    ApplyTabletValues(_mouseEvent);
                    PostEvent();
                    _lastButton = button;
                }
            }

            if (currButtonStates == 0)
            {
                // no buttons are pressed, reset button to 0
                CGEventSetIntegerValueField(_mouseEvent, CGEventField.mouseEventButtonNumber, 0);
            }
        }

        private static CGMouseButton ToCGMouseButton(MouseButton button)
        {
            return button switch
            {
                MouseButton.Left => CGMouseButton.kCGMouseButtonLeft,
                MouseButton.Right => CGMouseButton.kCGMouseButtonRight,
                MouseButton.Middle => CGMouseButton.kCGMouseButtonCenter,
                MouseButton.Backward => CGMouseButton.kCGMouseButtonBackward,
                MouseButton.Forward => CGMouseButton.kCGMouseButtonForward,
                _ => throw new ArgumentException("Invalid mouse button", nameof(button))
            };
        }

        private static CGEventType ToNoDragCGEventType(CGMouseButton button, bool state)
        {
            return (button, state) switch
            {
                (CGMouseButton.kCGMouseButtonLeft, true) => CGEventType.kCGEventLeftMouseDown,
                (CGMouseButton.kCGMouseButtonLeft, false) => CGEventType.kCGEventLeftMouseUp,
                (CGMouseButton.kCGMouseButtonRight, true) => CGEventType.kCGEventRightMouseDown,
                (CGMouseButton.kCGMouseButtonRight, false) => CGEventType.kCGEventRightMouseUp,
                (_, true) => CGEventType.kCGEventOtherMouseDown,
                (_, false) => CGEventType.kCGEventOtherMouseUp,
            };
        }

        private static CGEventType ToDragCGEventType(CGMouseButton button, bool state)
        {
            return (button, state) switch
            {
                (CGMouseButton.kCGMouseButtonLeft, true) => CGEventType.kCGEventLeftMouseDragged,
                (CGMouseButton.kCGMouseButtonRight, true) => CGEventType.kCGEventRightMouseDragged,
                (_, true) => CGEventType.kCGEventOtherMouseDragged,
                (_, false) => CGEventType.kCGEventMouseMoved,
            };
        }

        private static bool IsButtonSet(int buttonStates, CGMouseButton button)
        {
            return (buttonStates & (1 << (int)button)) != 0;
        }

        private static void SetButtonState(ref int buttonStates, CGMouseButton button, bool state)
        {
            if (state)
                buttonStates |= 1 << (int)button;
            else
                buttonStates &= ~(1 << (int)button);
        }

        private void ApplyTabletValues(IntPtr evt)
        {
            // Send proximity events if there are no reports for a while.
            if (_currButtonStates == 0 && _prevButtonStates == 0)
            {
                var elapsed = _stopWatch.ElapsedMilliseconds;
                _stopWatch.Restart();
                if (elapsed > ProximityExpiresDurationInMs)
                {
                    var pointerType = _isEraser ?? false
                        ? NSPointingDeviceType.Eraser
                        : NSPointingDeviceType.Pen;

                    PostProximityEvent();

                    CGEventSetIntegerValueField(evt, CGEventField.mouseEventSubtype, (long)CGMouseEventSubtype.TabletProximity);
                    CGEventSetIntegerValueField(evt, CGEventField.tabletProximityEventEnterProximity, 1);
                    CGEventSetIntegerValueField(evt, CGEventField.tabletProximityEventPointerType, (long)pointerType);
                    CGEventSetIntegerValueField(evt, CGEventField.tabletProximityEventCapabilityMask, (long)TabletCapabilityMask);
                    CGEventSetIntegerValueField(evt, CGEventField.tabletProximityEventDeviceID, DeviceId);
                    CGEventSetIntegerValueField(evt, CGEventField.tabletProximityEventVendorPointerType, VendorPointerType);

                    return;
                }
            }

            // Qt < 5.9 interprets kCGTabletEventPointButtons as left, right, middle mouse click.
            // see https://codereview.qt-project.org/c/qt/qtbase/+/180717
            int buttons = 0;
            if (IsButtonSet(_currButtonStates, CGMouseButton.kCGMouseButtonLeft))
                buttons |= 1;
            else if (IsButtonSet(_currButtonStates, CGMouseButton.kCGMouseButtonRight))
                buttons |= 2;
            else if (IsButtonSet(_currButtonStates, CGMouseButton.kCGMouseButtonCenter))
                buttons |= 4;

            CGEventSetDoubleValueField(evt, CGEventField.mouseEventPressure, _pressure ?? 1.0);

            CGEventSetIntegerValueField(evt, CGEventField.mouseEventSubtype, (long)CGMouseEventSubtype.TabletPoint);
            // The fields of tabletEvent can only be set after the subtype is defined.
            CGEventSetIntegerValueField(evt, CGEventField.tabletEventPointButtons, buttons);
            CGEventSetIntegerValueField(evt, CGEventField.tabletEventDeviceID, DeviceId);
            CGEventSetDoubleValueField(evt, CGEventField.tabletEventPointPressure, _pressure ?? 1.0);

            if (_tilt != null)
            {
                CGEventSetDoubleValueField(evt, CGEventField.tabletEventTiltX, _tilt.Value.X / 90.0);
                // TiltY is inverted on MacOS
                // see https://github.com/chromium/chromium/blob/62f1a92b04c1172431a64d581be9e64742c81576/content/browser/renderer_host/input/web_input_event_builders_mac.mm#L431
                CGEventSetDoubleValueField(evt, CGEventField.tabletEventTiltY, -_tilt.Value.Y / 90.0);
            }

            // This uses an undocumented flag that tells the system to automatically include the event flags in the event.
            // It's a better approach than fetching the active event flags ourselves, as doing so can introduce a race condition
            // (e.g., if a modifier key is released after we check its status but before the event is posted).
            // However, this flag has not effects for synthetic keyboard events, so we manually set the flags if there are modifiers bindings.

            if (_keyboard.getCurrentFlags() != 0)
                CGEventSetFlags(evt, _keyboard.getCurrentFlags());
            else
                CGEventSetFlags(evt, ~0U);
        }

        private void PostEvent()
        {
            CGEventPost(CGEventTapLocation.kCGHIDEventTap, _mouseEvent);
            // Fields in a CGEvent are stored in a union determined by the event type,
            // and they cannot be safely reused.
            CFRelease(_mouseEvent);
            _mouseEvent = CGEventCreate(_eventSource);
        }

        ~MacOSVirtualMouse()
        {
            if (_eventSource != IntPtr.Zero)
            {
                CFRelease(_eventSource);
            }
            if (_mouseEvent != IntPtr.Zero)
            {
                CFRelease(_mouseEvent);
            }
        }
    }
}
