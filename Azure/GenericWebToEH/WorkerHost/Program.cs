﻿namespace WorkerHost
{
    using System;
    using System.Configuration;
    using System.Threading;
    using Microsoft.Azure;
    using Microsoft.ConnectTheDots.Common;
    using Microsoft.ConnectTheDots.Gateway;
    using Microsoft.ServiceBus;
    using Microsoft.ServiceBus.Messaging;
    using Microsoft.WindowsAzure.ServiceRuntime;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;

    using ApiReaders;

    public class WorkerHost : RoleEntryPoint
    {
        private static readonly ILogger _logger = EventLogger.Instance;

        private static GatewayService gateway;

        static void Main()
        {
            try
            {
                StartHost();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }
        }

        public override void Run()
        {
            try
            {
                StartHost();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }
        }

        private static void StartHost()
        {
            _logger.LogInfo("Starting Worker...");

            int sleepTimeMs;
            if (!int.TryParse(CloudConfigurationManager.GetSetting("SleepTimeMs"), out sleepTimeMs))
            {
                _logger.LogInfo("Incorrect SleepTimeMs value, using default...");
                //default sleep time interval is 10 sec
                sleepTimeMs = 10000;
            }
            int sleepTimeOnExceptionMs = sleepTimeMs/2;
            
            NetworkCredential credentialToUse = new NetworkCredential(CloudConfigurationManager.GetSetting("UserName"),
                CloudConfigurationManager.GetSetting("Password"));

            bool useXML = CloudConfigurationManager.GetSetting("SendJson").ToLowerInvariant().Contains("false");

            string xmlTemplate = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).GetSection("MergeToXML").SectionInformation.GetRawXml();

            var readers = PrepareReaders(xmlTemplate, useXML, credentialToUse);

            string serviceBusConnectionString = ReadConfigValue("Microsoft.ServiceBus.EventHubConnectionString", "[event hub connection string]");
            string hubName = ReadConfigValue("Microsoft.ServiceBus.EventHubToUse", "[event hub name]");

            string messageSubject = CloudConfigurationManager.GetSetting("MessageSubject");
            string messageDeviceId = CloudConfigurationManager.GetSetting("MessageDeviceId");
            string messageDeviceDisplayName = CloudConfigurationManager.GetSetting("MessageDeviceDisplayName");

            AMQPConfig amqpDevicesConfig = PrepareAMQPConfig(serviceBusConnectionString, hubName, messageSubject, messageDeviceId, messageDeviceDisplayName);

            if (amqpDevicesConfig == null)
            {
                _logger.LogInfo("Not able to construct AMQP config for Event Hub using provided connection string...");
                return;
            }

            gateway = CreateGateway(amqpDevicesConfig);

            for (; ; )
            {
                try
                {
                    foreach (var reader in readers)
                    {
                        IEnumerable<string> dataEnumerable = reader.GetData();
                        foreach (string newDataJson in dataEnumerable)
                        {
                            if (newDataJson != null)
                            {
                                gateway.Enqueue(newDataJson);
                            }
                        }
                    }

                    Thread.Sleep(sleepTimeMs);
                }
                catch(Exception ex)
                {
                    Thread.Sleep(sleepTimeOnExceptionMs);
                    _logger.LogError(ex.Message);
                }
            }
        }

        private static string ReadConfigValue(string keyName, string defaultNotSetValue)
        {
            string value = CloudConfigurationManager.GetSetting(keyName);
            if (string.IsNullOrEmpty(value) || value.Equals(defaultNotSetValue))
            {
                value = ConfigurationManager.AppSettings.Get(keyName);
            }
            return value;
        }

        private static IEnumerable<RawXMLWithHeaderToJsonReader> PrepareReaders(string xmlTemplate, bool useXML, NetworkCredential credeitial)
        {
            List<RawXMLWithHeaderToJsonReader> result = new List<RawXMLWithHeaderToJsonReader>();
            var configItems = Loader.GetAPIConfigItems();
            foreach (var config in configItems)
            {
                result.Add(new RawXMLWithHeaderToJsonReader(xmlTemplate, useXML, config.APIAddress, credeitial));
            }
            return result;
        }

        private static AMQPConfig PrepareAMQPConfig(string connectionString, string hubName,
            string messageSubject, string messageDeviceId, string messageDeviceDisplayName)
        {
            NamespaceManager nsmgr = NamespaceManager.CreateFromConnectionString(connectionString);
            EventHubDescription desc = nsmgr.GetEventHub(hubName);

            foreach (var rule in desc.Authorization)
            {
                var accessAuthorizationRule = rule as SharedAccessAuthorizationRule;
                if (accessAuthorizationRule == null) continue;
                if (!accessAuthorizationRule.Rights.Contains(AccessRights.Send)) continue;

                string amqpAddress = string.Format("amqps://{0}:{1}@{2}",
                    accessAuthorizationRule.KeyName,
                    Uri.EscapeDataString(accessAuthorizationRule.PrimaryKey), nsmgr.Address.Host);

                AMQPConfig amqpConfig = new AMQPConfig
                {
                    AMQPSAddress = amqpAddress,
                    EventHubName = hubName,
                    EventHubDeviceDisplayName = string.IsNullOrEmpty(messageSubject) ? "SensorGatewayService" : messageSubject,
                    EventHubDeviceId = string.IsNullOrEmpty(messageDeviceId) ? "a94cd58f-4698-4d6a-b9b5-4e3e0f794618" : messageDeviceId,
                    EventHubMessageSubject = string.IsNullOrEmpty(messageDeviceDisplayName) ? "gtsv" : messageDeviceDisplayName
                };
                return amqpConfig;
            }
            return null;
        }

        private static GatewayService CreateGateway(AMQPConfig amqpConfig)
        {
            try
            {
                var _gatewayQueue = new GatewayQueue<QueuedItem>();

                var _AMPQSender = new AMQPSender<string>(
                                                    amqpConfig.AMQPSAddress,
                                                    amqpConfig.EventHubName,
                                                    amqpConfig.EventHubMessageSubject,
                                                    amqpConfig.EventHubDeviceId,
                                                    amqpConfig.EventHubDeviceDisplayName,
                                                    _logger
                                                    );

                var _batchSenderThread = new BatchSenderThread<QueuedItem, string>(
                                                    _gatewayQueue,
                                                    _AMPQSender,
                                                    null,
                                                    m => m.JsonData,
                                                    _logger);

                _batchSenderThread.Start();

                GatewayService service = new GatewayService(
                    _gatewayQueue,
                    _batchSenderThread
                )
                {
                    Logger = _logger
                };

                service.OnDataInQueue += (data) => _batchSenderThread.Process();
                _logger.Flush();

                return service;
            }
            catch (Exception ex)
            {
                _logger.LogError("Exception on creating Gateway: " + ex.Message);
            }

            return null;
        }
    }
}
