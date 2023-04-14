﻿using System.Text.Json;
using Api.Controllers.Models;
using Api.Database.Models;
using Api.Mqtt;
using Api.Mqtt.Events;
using Api.Mqtt.MessageModels;
using Api.Services;
using Api.Services.Models;
using Api.Utilities;
using Microsoft.IdentityModel.Tokens;

namespace Api.EventHandlers
{
    /// <summary>
    /// A background service which listens to events and performs callback functions.
    /// </summary>
    public class MqttEventHandler : EventHandlerBase
    {
        private readonly ILogger<MqttEventHandler> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        private IMissionService MissionService =>
            _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IMissionService>();
        private IRobotService RobotService =>
            _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IRobotService>();

        public MqttEventHandler(ILogger<MqttEventHandler> logger, IServiceScopeFactory scopeFactory)
        {
            _logger = logger;

            // Reason for using factory: https://www.thecodebuzz.com/using-dbcontext-instance-in-ihostedservice/
            _scopeFactory = scopeFactory;

            Subscribe();
        }

        public override void Subscribe()
        {
            MqttService.MqttIsarRobotStatusReceived += OnIsarRobotStatus;
            MqttService.MqttIsarRobotInfoReceived += OnIsarRobotInfo;
            MqttService.MqttIsarMissionReceived += OnMissionUpdate;
            MqttService.MqttIsarTaskReceived += OnTaskUpdate;
            MqttService.MqttIsarStepReceived += OnStepUpdate;
            MqttService.MqttIsarBatteryReceived += OnBatteryUpdate;
            MqttService.MqttIsarPressureReceived += OnPressureUpdate;
            MqttService.MqttIsarPoseReceived += OnPoseUpdate;
        }

        public override void Unsubscribe()
        {
            MqttService.MqttIsarRobotStatusReceived -= OnIsarRobotStatus;
            MqttService.MqttIsarRobotInfoReceived -= OnIsarRobotInfo;
            MqttService.MqttIsarMissionReceived -= OnMissionUpdate;
            MqttService.MqttIsarTaskReceived -= OnTaskUpdate;
            MqttService.MqttIsarStepReceived -= OnStepUpdate;
            MqttService.MqttIsarBatteryReceived -= OnBatteryUpdate;
            MqttService.MqttIsarPressureReceived -= OnPressureUpdate;
            MqttService.MqttIsarPoseReceived -= OnPoseUpdate;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await stoppingToken;
        }

        private async void OnIsarRobotStatus(object? sender, MqttReceivedArgs mqttArgs)
        {
            var isarRobotStatus = (IsarRobotStatusMessage)mqttArgs.Message;
            var robot = await RobotService.ReadByIsarId(isarRobotStatus.IsarId);

            if (robot == null)
            {
                _logger.LogInformation(
                    "Received message from unknown ISAR instance {id} with robot name {name}.",
                    isarRobotStatus.IsarId,
                    isarRobotStatus.RobotName
                );
                return;
            }

            if (robot.Status == isarRobotStatus.RobotStatus)
                return;

            robot.Status = isarRobotStatus.RobotStatus;
            robot = await RobotService.Update(robot);
            _logger.LogInformation(
                "Updated status for robot {name} to {status}",
                robot.Name,
                robot.Status
            );
        }

        private async void OnIsarRobotInfo(object? sender, MqttReceivedArgs mqttArgs)
        {
            var robotService = RobotService;
            var isarRobotInfo = (IsarRobotInfoMessage)mqttArgs.Message;
            var robot = await robotService.ReadByIsarId(isarRobotInfo.IsarId);

            if (robot == null)
            {
                _logger.LogInformation(
                    "Received message from new ISAR instance '{id}' with robot name '{name}'. Adding new robot to database.",
                    isarRobotInfo.IsarId,
                    isarRobotInfo.RobotName
                );

                var robotQuery = new CreateRobotQuery()
                {
                    IsarId = isarRobotInfo.IsarId,
                    Name = isarRobotInfo.RobotName,
                    Model = isarRobotInfo.RobotModel,
                    SerialNumber = isarRobotInfo.SerialNumber,
                    VideoStreams = isarRobotInfo.VideoStreamQueries,
                    Host = isarRobotInfo.Host,
                    Port = isarRobotInfo.Port,
                    Status = RobotStatus.Available,
                    Enabled = true
                };

                robot = await RobotService.Create(robotQuery);
                _logger.LogInformation(
                    "Added robot '{robotName}' with ISAR id '{isarId}' to database",
                    robot.Name,
                    robot.IsarId
                );

                return;
            }

            var updatedStreams = isarRobotInfo.VideoStreamQueries
                .Select(
                    stream =>
                        new VideoStream
                        {
                            Name = stream.Name,
                            Url = stream.Url,
                            Type = stream.Type
                        }
                )
                .ToList();

            List<string> updatedFields = new();
            if (
                !(
                    updatedStreams.Count == robot.VideoStreams.Count
                    && updatedStreams.TrueForAll(stream => robot.VideoStreams.Contains(stream))
                )
            )
            {
                updatedFields.Add(
                    $"\nVideoStreams ({JsonSerializer.Serialize(robot.VideoStreams, new JsonSerializerOptions() { WriteIndented = true })} "
                        + "\n-> "
                        + $"\n{JsonSerializer.Serialize(updatedStreams, new JsonSerializerOptions() { WriteIndented = true })})\n"
                );
                robot.VideoStreams = updatedStreams;
            }

            if (!isarRobotInfo.Host.Equals(robot.Host, StringComparison.Ordinal))
            {
                updatedFields.Add($"\nHost ({robot.Host} -> {isarRobotInfo.Host})\n");
                robot.Host = isarRobotInfo.Host;
            }

            if (!isarRobotInfo.Port.Equals(robot.Port))
            {
                updatedFields.Add($"\nPort ({robot.Port} -> {isarRobotInfo.Port})\n");
                robot.Port = isarRobotInfo.Port;
            }

            if (!updatedFields.IsNullOrEmpty())
            {
                robot = await robotService.Update(robot);
                _logger.LogInformation(
                    "Updated robot '{id}' ('{robotName}') in database: {updates}",
                    robot.Id,
                    robot.Name,
                    updatedFields
                );
            }
        }

        private async void OnMissionUpdate(object? sender, MqttReceivedArgs mqttArgs)
        {
            var isarMission = (IsarMissionMessage)mqttArgs.Message;
            MissionStatus status;
            try
            {
                status = Mission.MissionStatusFromString(isarMission.Status);
            }
            catch (ArgumentException e)
            {
                _logger.LogError(
                    e,
                    "Failed to parse mission status from MQTT message. Mission with ISARMissionId '{isarMissionId}' was not updated.",
                    isarMission.MissionId
                );
                return;
            }

            var flotillaMission = await MissionService.UpdateMissionStatusByIsarMissionId(
                isarMission.MissionId,
                status
            );

            if (flotillaMission is null)
            {
                _logger.LogError(
                    "No mission found with ISARMissionId '{isarMissionId}'. Could not update status to '{status}'",
                    isarMission.MissionId,
                    status
                );
                return;
            }

            _logger.LogInformation(
                "Mission '{id}' (ISARMissionID='{isarId}') status updated to '{status}' for robot '{robotName}' with ISAR id '{isarId}'",
                flotillaMission.Id,
                isarMission.MissionId,
                isarMission.Status,
                isarMission.RobotName,
                isarMission.IsarId
            );

            var robot = await RobotService.ReadByIsarId(isarMission.IsarId);
            if (robot is null)
            {
                _logger.LogError(
                    "Could not find robot '{robotName}' with ISAR id '{isarId}'. The robot status is not updated.",
                    isarMission.RobotName,
                    isarMission.IsarId
                );
                return;
            }

            robot.Status = flotillaMission.IsCompleted ? RobotStatus.Available : RobotStatus.Busy;
            if (flotillaMission.IsCompleted)
                robot.CurrentMissionId = null;

            await RobotService.Update(robot);
            _logger.LogInformation(
                "Robot '{id}' ('{name}') - status set to '{status}'.",
                robot.IsarId,
                robot.Name,
                robot.Status
            );
        }

        private async void OnTaskUpdate(object? sender, MqttReceivedArgs mqttArgs)
        {
            var task = (IsarTaskMessage)mqttArgs.Message;
            IsarTaskStatus status;
            try
            {
                status = IsarTask.StatusFromString(task.Status);
            }
            catch (ArgumentException e)
            {
                _logger.LogError(
                    e,
                    "Failed to parse mission status from MQTT message. Mission '{id}' was not updated.",
                    task.MissionId
                );
                return;
            }

            bool success = await MissionService.UpdateTaskStatusByIsarTaskId(
                task.MissionId,
                task.TaskId,
                status
            );

            if (success)
                _logger.LogInformation(
                    "Task '{id}' updated to '{status}' for robot '{robotName}' with ISAR id '{isarId}'",
                    task.TaskId,
                    task.Status,
                    task.RobotName,
                    task.IsarId
                );
        }

        private async void OnStepUpdate(object? sender, MqttReceivedArgs mqttArgs)
        {
            var step = (IsarStepMessage)mqttArgs.Message;

            // Flotilla does not care about DriveTo or localization steps
            var stepType = IsarStep.StepTypeFromString(step.StepType);
            if (stepType is IsarStepType.DriveToPose || stepType is IsarStepType.Localize)
                return;

            IsarStepStatus status;
            try
            {
                status = IsarStep.StatusFromString(step.Status);
            }
            catch (ArgumentException e)
            {
                _logger.LogError(
                    e,
                    "Failed to parse mission status from MQTT message. Mission '{id}' was not updated.",
                    step.MissionId
                );
                return;
            }

            bool success = await MissionService.UpdateStepStatusByIsarStepId(
                step.MissionId,
                step.TaskId,
                step.StepId,
                status
            );

            if (success)
                _logger.LogInformation(
                    "Inspection '{id}' updated to '{status}' for robot '{robotName}' with ISAR id '{isarId}'",
                    step.StepId,
                    step.Status,
                    step.RobotName,
                    step.IsarId
                );
        }

        private async void OnBatteryUpdate(object? sender, MqttReceivedArgs mqttArgs)
        {
            var batteryStatus = (IsarBatteryMessage)mqttArgs.Message;
            var robot = await RobotService.ReadByIsarId(batteryStatus.IsarId);
            if (robot == null)
            {
                _logger.LogWarning(
                    "Could not find corresponding robot for battery update on robot '{robotName}' with ISAR id '{isarId}'",
                    batteryStatus.RobotName,
                    batteryStatus.IsarId
                );
            }
            else
            {
                robot.BatteryLevel = batteryStatus.BatteryLevel;
                await RobotService.Update(robot);
                _logger.LogDebug(
                    "Updated battery on robot '{robotName}' with ISAR id '{isarId}'",
                    robot.Name,
                    robot.IsarId
                );
            }
        }

        private async void OnPressureUpdate(object? sender, MqttReceivedArgs mqttArgs)
        {
            var pressureStatus = (IsarPressureMessage)mqttArgs.Message;
            var robot = await RobotService.ReadByIsarId(pressureStatus.IsarId);
            if (robot == null)
            {
                _logger.LogWarning(
                    "Could not find corresponding robot for pressure update on robot '{robotName}' with ISAR id '{isarId}'",
                    pressureStatus.RobotName,
                    pressureStatus.IsarId
                );
            }
            else
            {
                robot.PressureLevel = pressureStatus.PressureLevel;
                await RobotService.Update(robot);
                _logger.LogDebug(
                    "Updated pressure on '{robotName}' with ISAR id '{isarId}'",
                    robot.Name,
                    robot.IsarId
                );
            }
        }

        private async void OnPoseUpdate(object? sender, MqttReceivedArgs mqttArgs)
        {
            var poseStatus = (IsarPoseMessage)mqttArgs.Message;
            var robot = await RobotService.ReadByIsarId(poseStatus.IsarId);
            if (robot == null)
            {
                _logger.LogWarning(
                    "Could not find corresponding robot for pose update on robot '{robotName}' with ISAR id '{isarId}'",
                    poseStatus.RobotName,
                    poseStatus.IsarId
                );
            }
            else
            {
                try
                {
                    poseStatus.Pose.CopyIsarPoseToRobotPose(robot.Pose);
                }
                catch (NullReferenceException e)
                {
                    _logger.LogWarning(
                        "NullReferenceException while updating pose on robot '{robotName}' with ISAR id '{isarId}': {message}",
                        robot.Name,
                        robot.IsarId,
                        e.Message
                    );
                }

                await RobotService.Update(robot);
                _logger.LogDebug(
                    "Updated pose on robot '{robotName}' with ISAR id '{isarId}'",
                    robot.Name,
                    robot.IsarId
                );
            }
        }
    }
}
