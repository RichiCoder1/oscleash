using System.Net;

namespace OSCLeash.App.Services;

internal class StatusService
{
    public event EventHandler<VrcChangedEventArgs>? VrcClientChanged;
    public event EventHandler<LeashUpdatedEventArgs>? LeashUpdated;
    public event EventHandler<ErrorEventArgs>? Error;
    
    public void OnVrcClientStatus(bool connected, IPAddress? ipAddress)
    {
        VrcClientChanged?.Invoke(this, new VrcChangedEventArgs(connected, ipAddress));
    }
    
    public void OnLeashUpdated(bool active, float speed, bool isWalking)
    {
        LeashUpdated?.Invoke(this, new LeashUpdatedEventArgs(active, speed, isWalking));
    }
    
    public void OnError(Exception exception)
    {
        Error?.Invoke(this, new ErrorEventArgs(exception));
    }
}

internal record VrcChangedEventArgs(bool Connected, IPAddress? IpAddress);
internal record LeashUpdatedEventArgs(bool IsActive, float Speed, bool IsWalking);
internal record ErrorEventArgs(Exception Exception);