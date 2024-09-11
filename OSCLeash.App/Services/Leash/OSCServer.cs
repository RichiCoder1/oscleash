using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using LucHeart.CoreOSC;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OscQueryLibrary;

namespace OSCLeash.App.Services.Leash;

internal class OSCServer(ILogger<OSCServer> logger, ISettingsService settingsService, StatusService statusService) : IHostedService
{
    private OscDuplex? _currentVRCSession;
    private CancellationTokenSource? _rootCancellationTokenSource;
    private CancellationTokenSource? _messageCancellationTokenSource;
    private OscQueryServer? _server;
    private readonly Subject<string> _leashUpdates = new();
    private readonly Subject<(bool isGrabbed, float speed, bool isRunning)> _movementUpdated = new();

    private const string LeashAddressPrefix = "/avatar/parameters/OSCLeash/";

    internal readonly ConcurrentDictionary<string, Leash> LeashData = new();
    internal bool IsVrcConnected => _currentVRCSession != null;
    internal string VrcIpAddress { get; private set; } = string.Empty;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = settingsService.Value;

        if (!IPAddress.TryParse(settings.IP, out var ip))
        {
            logger.LogError("Invalid IP address provided in settings");
            return Task.CompletedTask;
        }

        _server = new OscQueryServer("OSCLeash", ip ?? IPAddress.Loopback);
        _server.FoundVrcClient += ServerOnFoundVrcClient;
        _server.ParameterUpdate += ServerOnParameterUpdate;

        _leashUpdates
            .Buffer(TimeSpan.FromMilliseconds(66))
            .Select(leashes => leashes.Distinct().ToList())
            .Where(_ =>  IsVrcConnected)
            .Select(leashes => Observable.FromAsync(async () => await ProcessLeashUpdates(leashes)))
            .Concat()
            .Subscribe();

        _movementUpdated
            .DistinctUntilChanged()
            .Subscribe(x => statusService.OnLeashUpdated(x.isGrabbed, x.speed, x.isRunning));

        _rootCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken.Register(() =>
        {
            logger.LogInformation("Stopping OSC Server...");
            _server.Dispose();
        });

        logger.LogInformation("Starting OSC Server...");
        _server.Start();

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _leashUpdates.Dispose();
        _server?.Dispose();
        _currentVRCSession?.Dispose();
        await _rootCancellationTokenSource!.CancelAsync();
    }

    private async Task SendMessage(string address, object value)
    {
        if (_currentVRCSession == null)
        {
            logger.LogWarning("No VRC client connected, skipping message sending");
            return;
        }
        await _currentVRCSession.SendAsync(new OscMessage(address, value));
    }

    private Task ServerOnFoundVrcClient(OscQueryServer queryServer, IPEndPoint endPoint)
    {
        logger.LogInformation("Found VRC Client at {Client}", endPoint);
        // Clean up the previous session
        _messageCancellationTokenSource?.Cancel();
        _messageCancellationTokenSource?.Dispose();
        _currentVRCSession?.Dispose();
        _currentVRCSession = null;

        _currentVRCSession = new OscDuplex(
            new IPEndPoint(endPoint.Address, queryServer.OscReceivePort), endPoint);
        VrcIpAddress = endPoint.Address.ToString();

        _messageCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_rootCancellationTokenSource!.Token);
        Task.Run(ListenForMessages, _messageCancellationTokenSource!.Token);

        statusService.OnVrcClientStatus(true, endPoint.Address);
        
        return Task.CompletedTask;
    }

    private async Task ListenForMessages()
    {
        try
        {
            if (_currentVRCSession == null)
            {
                logger.LogWarning("No VRC client connected, skipping message listening");
                return;
            }

            while (!_messageCancellationTokenSource!.IsCancellationRequested)
            {
                var message = await _currentVRCSession.ReceiveMessageAsync();

                if (!message.Address.StartsWith(LeashAddressPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (leashName, leashDirection, parameterKey) = GetParameterInfo(message.Address);

                // Angle is very noise and unused, so we just skip it.
                if (parameterKey == "Angle")
                {
                    continue;
                }

                LeashData.AddOrUpdate(leashName, new Leash(leashName, leashDirection),
                    (_, leash) => leash.WithParameter(parameterKey, message.Arguments[0]));
                
                _leashUpdates.OnNext(leashName);
            }
        }
        catch (SocketException) when (_messageCancellationTokenSource?.IsCancellationRequested is true)
        {
            // Ignore the exception if the cancellation token is triggered
        }
        catch (SocketException ex) when (ex.Message.Contains("remote host"))
        {
            logger.LogInformation("VRC Client disconnected, stopping message listening");
            _messageCancellationTokenSource?.Dispose();
            _currentVRCSession?.Dispose();
            _currentVRCSession = null;
            
            statusService.OnVrcClientStatus(false, null);
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while listening for messages");
            Environment.Exit(-1);
        }
    }

    private Task ServerOnParameterUpdate(Dictionary<string, object?> parameters, string avatarId)
    {
        logger.LogDebug("Received parameter update for avatar {AvatarId}", avatarId);
        foreach (var (key, value) in parameters)
        {
            if (!key.StartsWith(LeashAddressPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (leashName, leashDirection, parameterKey) = GetParameterInfo(key);

            // Angle is very noise and unused, so we just skip it.
            if (parameterKey == "Angle")
            {
                continue;
            }

            LeashData.AddOrUpdate(leashName, new Leash(leashName, leashDirection),
                (_, leash) => leash.WithParameter(parameterKey, value));
            
            _leashUpdates.OnNext(leashName);
        }

        return Task.CompletedTask;
    }

    private async Task ProcessLeashUpdates(IList<string> leashes)
    {
        if (leashes.Count == 0)
        {
            return;
        }
        
        var grabbedLeash = LeashData.FirstOrDefault(x => x.Value.IsGrabbed);
        var activeLeash = grabbedLeash.Value is not default(Leash) ? grabbedLeash : LeashData.FirstOrDefault();

        if (activeLeash.Value is not default(Leash))
        {
            // Don't process updates for the non-active leashes
            if (!leashes.Contains(activeLeash.Key))
            {
                return;
            }
            await CalculateLeashUpdate(activeLeash.Value);
        }
        else
        {
            await SendMovementUpdate(0, 0, 0, true);
        }
    }

    private async Task CalculateLeashUpdate(Leash leash)
    {
        var settings = settingsService.Value;

        var outputMultiplier = leash.Stretch * settings.StrengthMultiplier;
        var verticalMove = Math.Clamp((leash.ZPos - leash.ZNeg) * outputMultiplier, -1, 1);
        var horizontalMove = Math.Clamp((leash.XPos - leash.XNeg) * outputMultiplier, -1, 1);

        var yCombined = leash.YPos + leash.YNeg;
        if (yCombined >= settings.UpDownDeadzone)
        {
            verticalMove = 0.0f;
            horizontalMove = 0.0f;
        }

        if (settings.UpDownCompensation != 0)
        {
            var yModifier = Math.Clamp(1.0f - yCombined * settings.UpDownCompensation, -1, 1);
            if (yModifier != 0.0f)
            {
                verticalMove /= yModifier;
                horizontalMove /= yModifier;
            }
        }

        var turnSpeed = 0.0f;
        if (settings.TurningEnabled && leash.Stretch > settings.TurningDeadzone && leash.Direction != LeashDirection.Unknown)
        {
            turnSpeed = settings.TurningMultiplier;
            var turnGoal = settings.TurningGoal / 180.0f;
            switch (leash.Direction)
            {
                case LeashDirection.North when leash.ZPos < turnGoal:
                    turnSpeed *= horizontalMove;
                    if (leash.XPos > leash.XNeg)
                    {
                        turnSpeed += leash.ZNeg;
                    }
                    else
                    {
                        turnSpeed -= leash.ZNeg;
                    }
                    break;
                case LeashDirection.South when leash.ZNeg < turnGoal:
                    turnSpeed *= -horizontalMove;
                    if (leash.XPos > leash.XNeg)
                    {
                        turnSpeed -= leash.ZPos;
                    }
                    else
                    {
                        turnSpeed += leash.ZPos;
                    }
                    break;
                case LeashDirection.East when leash.XPos < turnGoal:
                    turnSpeed *= verticalMove;
                    if (leash.ZPos > leash.ZNeg)
                    {
                        turnSpeed += leash.XNeg;
                    }
                    else
                    {
                        turnSpeed -= leash.XNeg;
                    }
                    break;
                case LeashDirection.West when leash.XNeg < turnGoal:
                    turnSpeed *= -verticalMove;
                    if (leash.ZPos > leash.ZNeg)
                    {
                        turnSpeed -= leash.XPos;
                    }
                    else
                    {
                        turnSpeed += leash.XPos;
                    }
                    break;
                case LeashDirection.Unknown:
                default:
                    turnSpeed = 0.0f;
                    break;
            }

            turnSpeed = Math.Clamp(turnSpeed, -1, 1);
        }

        logger.LogDebug("Leash Calc: grabbed => {isGrabbed}, stretch => {stretch}", leash.IsGrabbed, leash.Stretch);

        if (leash.IsGrabbed)
        {
            if (leash.Stretch > settings.RunDeadzone)
            {
                await SendMovementUpdate(verticalMove, horizontalMove, turnSpeed, true);
            }
            else if (leash.Stretch > settings.WalkDeadzone)
            {
                await SendMovementUpdate(verticalMove, horizontalMove, turnSpeed, false);
            }
            else
            {
                await SendMovementUpdate(0, 0, 0, false);
            }
        }
        else
        {
            await SendMovementUpdate(0, 0, 0, false);
        }
    }

    private async Task SendMovementUpdate(float verticalMove, float horizontalMove, float turn, bool isRunning)
    {
        logger.LogDebug("Move Update: vertical => {verticalMove:F2}, horizontal => {horizontalMove:F2}, turn => {turn:F2}, running => {isRunning}", verticalMove, horizontalMove, turn, isRunning);

        await SendMessage("/input/Vertical", verticalMove);
        await SendMessage("/input/Horizontal", horizontalMove);
        if (settingsService.Value.TurningEnabled)
        {
            await SendMessage("/input/LookHorizontal", turn);
        }
        await SendMessage("/input/Run", isRunning ? 1 : 0);

        _movementUpdated.OnNext((verticalMove != 0 || horizontalMove != 0, Math.Max(Math.Abs(verticalMove), Math.Abs(horizontalMove)), isRunning));
    }

    private static (string, LeashDirection, string) GetParameterInfo(string parameterAddress)
    {
        var leashKey = parameterAddress[LeashAddressPrefix.Length..];
        var leashSplit = leashKey.Split('_');
        return leashSplit.Length switch
        {
            2 => (leashSplit[0], LeashDirection.Unknown, leashSplit[1]),
            3 when Enum.TryParse<LeashDirection>(leashSplit[1], true, out var direction) => (leashSplit[0], direction, leashSplit[2]),
            3 => (leashSplit[0], LeashDirection.Unknown, leashSplit[2]),
            _ => (string.Empty, LeashDirection.Unknown, string.Empty)
        };
    }
}