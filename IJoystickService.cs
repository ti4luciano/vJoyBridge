// ============================================================================
// IJoystickService.cs
// ============================================================================
using System;
using vJoyInterfaceWrap;

namespace vJoyBridge
{
    public interface IJoystickService
    {
        bool Initialize(uint deviceId);
        void SetAxis(uint deviceId, HID_USAGES axis, int value);
        void SetButton(uint deviceId, uint buttonId, bool state);
        void Shutdown(uint deviceId);
        event Action<int, int> OnForceFeedbackReceived;
    }
}