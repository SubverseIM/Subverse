﻿using Mono.Nat;
using System.Net;

namespace Subverse.Server
{
    internal class HubService : BackgroundService
    {
        private readonly IPeerService _peerService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<HubService> _logger;

        private INatDevice? _natDevice;
        private Mapping? _mapping;

        public HubService(IPeerService peerService, IConfiguration configuration, ILogger<HubService> logger)
        {
            _peerService = peerService;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_configuration.GetSection("HubService")
                .GetValue<string?>("Hostname") is null) 
            {
                return;
            }

            NatUtility.DeviceFound += NatDeviceFound;
            NatUtility.StartDiscovery();

            try
            {
                await _peerService.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }

            try
            {
                if (_natDevice is not null && _mapping is not null)
                {
                    await _natDevice.DeletePortMapAsync(_mapping);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, null);
            }

            NatUtility.StopDiscovery();
        }

        private async void NatDeviceFound(object? sender, DeviceEventArgs e)
        {
            try
            {
                _natDevice = e.Device; 
                _mapping = await _natDevice.CreatePortMapAsync(
                    new(Protocol.Udp, 5060, 60303, 0, "SubverseV2")
                    );

                int remotePort = _mapping.PublicPort;
                IPAddress remoteAddr = await _natDevice.GetExternalIPAsync();

                if (remoteAddr != IPAddress.Any)
                {
                    _peerService.RemoteEndPoint = new IPEndPoint(remoteAddr, remotePort);
                    await _peerService.InitializeDhtAsync();
                }

                _logger.LogInformation($"Successfully allocated external endpoint: {_peerService.RemoteEndPoint}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, null);
            }
        }
    }
}
