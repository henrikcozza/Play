using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UniInject;
using UniRx;

// Disable warning about fields that are never assigned, their values are injected.
#pragma warning disable CS0649

public class ClientSideConnectRequestManager : AbstractSingletonBehaviour, INeedInjection, IClientSideConnectRequestManager
{
    public static ClientSideConnectRequestManager Instance => DontDestroyOnLoadManager.Instance.FindComponentOrThrow<ClientSideConnectRequestManager>();

    [Inject]
    private Settings settings;

    private const float ConnectRequestPauseInSeconds = 1f;
    private float nextConnectRequestTime;

    private readonly Subject<ConnectEvent> connectEventStream = new Subject<ConnectEvent>();
    public IObservable<ConnectEvent> ConnectEventStream => connectEventStream;
    
    private UdpClient clientUdpClient;

    private bool isListeningForConnectResponse;

    private bool hasBeenDestroyed;

    private int connectRequestCount;

    private readonly ConcurrentQueue<ConnectResponseDto> connectResponseQueue = new();

    private bool isApplicationPaused;

    private Thread acceptMessageFromServerThread;

    private ConnectedServerHandler connectedServerHandler;
    public bool IsConnected => connectedServerHandler != null;

    protected override object GetInstance()
    {
        return Instance;
    }

    protected override void StartSingleton()
    {
        clientUdpClient = !settings.OwnHost.IsNullOrEmpty()
            ? new UdpClient(new IPEndPoint(IPAddress.Parse(settings.OwnHost), settings.UdpPortOnClient))
            : new UdpClient(settings.UdpPortOnClient);

        acceptMessageFromServerThread = new Thread(poolHandle =>
        {
            while (!hasBeenDestroyed)
            {
                ClientAcceptMessageFromServer();
            }
        });
        acceptMessageFromServerThread.Start();
    }

    private void Update()
    {
        while (connectResponseQueue.TryDequeue(out ConnectResponseDto connectResponseDto))
        {
            if (connectResponseDto.ErrorMessage.IsNullOrEmpty()
                && connectResponseDto.MessagingPort > 0)
            {
                DisposeConnectedServerHandler();
                IPEndPoint messagingIpEndPoint = new(connectResponseDto.ServerIpEndPoint.Address, connectResponseDto.MessagingPort);
                connectedServerHandler = new(this, messagingIpEndPoint);

                connectEventStream.OnNext(new ConnectEvent
                {
                    IsSuccess = true,
                    ConnectRequestCount = connectRequestCount,
                    MessagingPort = connectResponseDto.MessagingPort,
                    HttpServerPort = connectResponseDto.HttpServerPort,
                    ServerIpEndPoint = connectResponseDto.ServerIpEndPoint,
                });
                connectRequestCount = 0;
            }
            else if (!connectResponseDto.ErrorMessage.IsNullOrEmpty())
            {
                DisposeConnectedServerHandler();
                connectEventStream.OnNext(new ConnectEvent
                {
                    ConnectRequestCount = connectRequestCount,
                    ErrorMessage = connectResponseDto.ErrorMessage,
                });
            }
        }
        
        if (connectedServerHandler == null
            && Time.time > nextConnectRequestTime
            && !isApplicationPaused)
        {
            nextConnectRequestTime = Time.time + ConnectRequestPauseInSeconds;
            ClientSendConnectRequest();
        }
    }

    private void DisposeConnectedServerHandler()
    {
        if (connectedServerHandler != null)
        {
            connectedServerHandler.Dispose();
            connectedServerHandler = null;
        }
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        isApplicationPaused = pauseStatus;
        if (pauseStatus
            && !Application.isEditor
            && Application.platform != RuntimePlatform.WindowsPlayer)
        {
            // Application is paused now (e.g. the app was moved to the background on Android)
            CloseConnectionAndReconnect();
        }
    }

    private void ClientAcceptMessageFromServer()
    {
        try
        {
            Debug.Log("Client listening for connect response on " + clientUdpClient.Client.LocalEndPoint);
            IPEndPoint serverIpEndPoint = new IPEndPoint(IPAddress.Any, 0);
            // Receive is a blocking call
            byte[] receivedBytes = clientUdpClient.Receive(ref serverIpEndPoint);
            string message = Encoding.UTF8.GetString(receivedBytes);
            HandleConnectResponse(serverIpEndPoint, message);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    private void HandleConnectResponse(IPEndPoint serverIpEndPoint, string message)
    {
        Debug.Log($"Received connect response from server {serverIpEndPoint} ({serverIpEndPoint.Address}): '{message}'");
        try
        {
            ConnectResponseDto connectResponseDto = JsonConverter.FromJson<ConnectResponseDto>(message);
            if (!connectResponseDto.ErrorMessage.IsNullOrEmpty())
            {
                throw new ConnectRequestException("Server returned error message: " + connectResponseDto.ErrorMessage);
            }
            if (connectResponseDto.ClientName.IsNullOrEmpty())
            {
                throw new ConnectRequestException("Malformed ConnectResponse: missing ClientName.");
            }
            if (connectResponseDto.ClientId.IsNullOrEmpty())
            {
                throw new ConnectRequestException("Malformed ConnectResponse: missing ClientId.");
            }
            if (!string.Equals(connectResponseDto.ClientId, settings.ClientId, StringComparison.InvariantCulture))
            {
                throw new ConnectRequestException($"Malformed ConnectResponse: wrong ClientId. Is {connectResponseDto.ClientId}, expected {settings.ClientId}");
            }
            if (connectResponseDto.MessagingPort <= 0)
            {
                throw new ConnectRequestException("Malformed ConnectResponse: invalid MessagingPort.");
            }

            connectResponseDto.ServerIpEndPoint = serverIpEndPoint;
            connectResponseQueue.Enqueue(connectResponseDto);
        }
        catch (Exception e)
        {
            connectResponseQueue.Enqueue(new ConnectResponseDto
            {
                ErrorMessage = e.Message
            });
        }
    }
    
    private void ClientSendConnectRequest()
    {
        if (connectRequestCount > 0)
        {
            // Last attempt failed
            connectEventStream.OnNext(new ConnectEvent
            {
                IsSuccess = false,
                ConnectRequestCount = connectRequestCount,
            });
        }
        
        connectRequestCount++;
        try
        {
            ConnectRequestDto connectRequestDto = new ConnectRequestDto
            {
                ProtocolVersion = ProtocolVersions.ProtocolVersion,
                ClientName = settings.ClientName,
                ClientId = settings.ClientId,
            };
            byte[] requestBytes = Encoding.UTF8.GetBytes(connectRequestDto.ToJson());
            // UDP Broadcast (255.255.255.255)
            clientUdpClient.Send(requestBytes, requestBytes.Length, "255.255.255.255", settings.UdpPortOnServer);
            Debug.Log($"Client has sent ConnectRequest as broadcast. Request: {connectRequestDto.ToJson()}");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    protected override void OnDestroySingleton()
    {
        hasBeenDestroyed = true;
        DisposeConnectedServerHandler();
        clientUdpClient?.Close();
    }

    public void CloseConnectionAndReconnect()
    {
        DisposeConnectedServerHandler();
        connectEventStream.OnNext(new ConnectEvent
        {
            IsSuccess = false,
        });
    }

    public bool TryGetConnectedServerHandler(out IConnectedServerHandler localConnectedServerHandler)
    {
        localConnectedServerHandler = this.connectedServerHandler;
        return localConnectedServerHandler != null;
    }

    public void RemoveConnectedServerHandler(IConnectedServerHandler localConnectedServerHandler)
    {
        if (this.connectedServerHandler != null
            && this.connectedServerHandler == localConnectedServerHandler)
        {
            DisposeConnectedServerHandler();
        }
    }
}
