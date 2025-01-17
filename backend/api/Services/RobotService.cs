﻿using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Api.Controllers.Models;
using Api.Database.Context;
using Api.Database.Models;
using Api.Utilities;
using Microsoft.EntityFrameworkCore;
namespace Api.Services
{
    public interface IRobotService
    {
        public Task<Robot> Create(Robot newRobot);
        public Task<Robot> CreateFromQuery(CreateRobotQuery robotQuery);
        public Task<IEnumerable<Robot>> ReadAll();
        public Task<IEnumerable<string>> ReadAllActivePlants();
        public Task<Robot?> ReadById(string id);
        public Task<Robot?> ReadByIsarId(string isarId);
        public Task<IList<Robot>> ReadLocalizedRobotsForInstallation(string installationCode);
        public Task<Robot> Update(Robot robot);
        public Task<Robot> UpdateRobotStatus(string robotId, RobotStatus status);
        public Task<Robot> UpdateRobotBatteryLevel(string robotId, float batteryLevel);
        public Task<Robot> UpdateRobotPressureLevel(string robotId, float? pressureLevel);
        public Task<Robot> UpdateRobotPose(string robotId, Pose pose);
        public Task<Robot> UpdateRobotEnabled(string robotId, bool enabled);
        public Task<Robot> UpdateCurrentMissionId(string robotId, string? missionId);
        public Task<Robot> UpdateCurrentArea(string robotId, Area? area);
        public Task<Robot> UpdateMissionQueueFrozen(string robotId, bool missionQueueFrozen);
        public Task<Robot?> Delete(string id);
        public Task SetRobotOffline(string robotId);
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal StringComparison",
        Justification = "EF Core refrains from translating string comparison overloads to SQL"
    )]
    public class RobotService(
        FlotillaDbContext context,
        ILogger<RobotService> logger,
        IRobotModelService robotModelService,
        ISignalRService signalRService,
        IAccessRoleService accessRoleService,
        IInstallationService installationService,
        IAreaService areaService,
        IMissionRunService missionRunService) : IRobotService, IDisposable
    {
        private readonly Semaphore _robotSemaphore = new(1, 1);

        public void Dispose()
        {
            _robotSemaphore.Dispose();
            GC.SuppressFinalize(this);
        }

        public async Task<Robot> Create(Robot newRobot)
        {
            if (newRobot.CurrentArea is not null) context.Entry(newRobot.CurrentArea).State = EntityState.Unchanged;

            await context.Robots.AddAsync(newRobot);
            await ApplyDatabaseUpdate(newRobot.CurrentInstallation);
            return newRobot;
        }

        public async Task<Robot> CreateFromQuery(CreateRobotQuery robotQuery)
        {
            var robotModel = await robotModelService.ReadByRobotType(robotQuery.RobotType);
            if (robotModel != null)
            {
                var installation = await installationService.ReadByName(robotQuery.CurrentInstallationCode);
                if (installation is null)
                {
                    logger.LogError("Installation {CurrentInstallation} does not exist", robotQuery.CurrentInstallationCode);
                    throw new DbUpdateException($"Could not create new robot in database as installation {robotQuery.CurrentInstallationCode} doesn't exist");
                }

                Area? area = null;
                if (robotQuery.CurrentAreaName is not null)
                {
                    area = await areaService.ReadByInstallationAndName(robotQuery.CurrentInstallationCode, robotQuery.CurrentAreaName);
                    if (area is null)
                    {
                        logger.LogError("Area '{AreaName}' does not exist in installation {CurrentInstallation}", robotQuery.CurrentAreaName, robotQuery.CurrentInstallationCode);
                        throw new DbUpdateException($"Could not create new robot in database as area '{robotQuery.CurrentAreaName}' does not exist in installation {robotQuery.CurrentInstallationCode}");
                    }
                }

                var newRobot = new Robot(robotQuery, installation, area)
                {
                    Model = robotModel
                };
                context.Entry(robotModel).State = EntityState.Unchanged;
                if (newRobot.CurrentArea is not null) context.Entry(newRobot.CurrentArea).State = EntityState.Unchanged;

                await context.Robots.AddAsync(newRobot);
                await ApplyDatabaseUpdate(newRobot.CurrentInstallation);
                _ = signalRService.SendMessageAsync("Robot added", newRobot!.CurrentInstallation, new RobotResponse(newRobot!));
                return newRobot!;
            }
            throw new DbUpdateException("Could not create new robot in database as robot model does not exist");
        }

        public async Task<Robot> UpdateRobotStatus(string robotId, RobotStatus status)
        {
            var robotQuery = context.Robots.Where(robot => robot.Id == robotId).Include(robot => robot.CurrentInstallation);
            var robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);

            await VerifyThatUserIsAuthorizedToUpdateDataForInstallation(robot!.CurrentInstallation);

            await robotQuery.ExecuteUpdateAsync(robots => robots.SetProperty(r => r.Status, status));

            robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);
            NotifySignalROfUpdatedRobot(robot!, robot!.CurrentInstallation!);

            return robot;
        }

        public async Task<Robot> UpdateRobotBatteryLevel(string robotId, float batteryLevel)
        {
            var robotQuery = context.Robots.Where(robot => robot.Id == robotId).Include(robot => robot.CurrentInstallation);
            var robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);

            await VerifyThatUserIsAuthorizedToUpdateDataForInstallation(robot!.CurrentInstallation);

            await robotQuery.ExecuteUpdateAsync(robots => robots.SetProperty(r => r.BatteryLevel, batteryLevel));

            robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);
            NotifySignalROfUpdatedRobot(robot!, robot!.CurrentInstallation!);

            return robot;
        }

        public async Task<Robot> UpdateRobotPressureLevel(string robotId, float? pressureLevel)
        {
            var robotQuery = context.Robots.Where(robot => robot.Id == robotId).Include(robot => robot.CurrentInstallation);
            var robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);

            await VerifyThatUserIsAuthorizedToUpdateDataForInstallation(robot!.CurrentInstallation);

            await robotQuery.ExecuteUpdateAsync(robots => robots.SetProperty(r => r.PressureLevel, pressureLevel));

            robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);
            NotifySignalROfUpdatedRobot(robot!, robot!.CurrentInstallation!);

            return robot;
        }

        private void ThrowIfRobotIsNull(Robot? robot, string robotId)
        {
            if (robot is not null) return;

            string errorMessage = $"Robot with ID {robotId} was not found in the database";
            logger.LogError("{Message}", errorMessage);
            throw new RobotNotFoundException(errorMessage);
        }

        public async Task<Robot> UpdateRobotPose(string robotId, Pose pose)
        {
            var robotQuery = context.Robots
                .Where(robot => robot.Id == robotId)
                .Include(robot => robot.CurrentInstallation);
            var robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);

            await VerifyThatUserIsAuthorizedToUpdateDataForInstallation(robot!.CurrentInstallation);

            await robotQuery
                .Select(r => r.Pose)
                .ExecuteUpdateAsync(poses => poses
                .SetProperty(p => p.Orientation.X, pose.Orientation.X)
                .SetProperty(p => p.Orientation.Y, pose.Orientation.Y)
                .SetProperty(p => p.Orientation.Z, pose.Orientation.Z)
                .SetProperty(p => p.Orientation.W, pose.Orientation.W)
                .SetProperty(p => p.Position.X, pose.Position.X)
                .SetProperty(p => p.Position.Y, pose.Position.Y)
                .SetProperty(p => p.Position.Z, pose.Position.Z)
            );

            robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);
            NotifySignalROfUpdatedRobot(robot!, robot!.CurrentInstallation!);

            return robot;
        }

        public async Task<Robot> UpdateRobotEnabled(string robotId, bool enabled)
        {
            var robotQuery = context.Robots.Where(robot => robot.Id == robotId).Include(robot => robot.CurrentInstallation);
            var robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);

            await VerifyThatUserIsAuthorizedToUpdateDataForInstallation(robot!.CurrentInstallation);

            await robotQuery.ExecuteUpdateAsync(robots => robots.SetProperty(r => r.Enabled, enabled));

            robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);
            NotifySignalROfUpdatedRobot(robot!, robot!.CurrentInstallation!);

            return robot;
        }

        public async Task<Robot> UpdateCurrentMissionId(string robotId, string? currentMissionId)
        {
            var robotQuery = context.Robots.Where(robot => robot.Id == robotId).Include(robot => robot.CurrentInstallation);
            var robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);

            await VerifyThatUserIsAuthorizedToUpdateDataForInstallation(robot!.CurrentInstallation);

            await robotQuery.ExecuteUpdateAsync(robots => robots.SetProperty(r => r.CurrentMissionId, currentMissionId));

            robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);
            NotifySignalROfUpdatedRobot(robot!, robot!.CurrentInstallation!);

            return robot;
        }

        public async Task<Robot> UpdateCurrentArea(string robotId, Area? area) { return await UpdateRobotProperty(robotId, "CurrentArea", area); }

        public async Task<Robot> UpdateMissionQueueFrozen(string robotId, bool missionQueueFrozen)
        {
            var robotQuery = context.Robots.Where(robot => robot.Id == robotId).Include(robot => robot.CurrentInstallation);
            var robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);

            await VerifyThatUserIsAuthorizedToUpdateDataForInstallation(robot!.CurrentInstallation);

            await robotQuery.ExecuteUpdateAsync(robots => robots.SetProperty(r => r.MissionQueueFrozen, missionQueueFrozen));

            robot = await robotQuery.FirstOrDefaultAsync();
            ThrowIfRobotIsNull(robot, robotId);
            NotifySignalROfUpdatedRobot(robot!, robot!.CurrentInstallation!);

            return robot;
        }

        public async Task<IEnumerable<Robot>> ReadAll() { return await GetRobotsWithSubModels().ToListAsync(); }

        public async Task<Robot?> ReadById(string id) { return await GetRobotsWithSubModels().FirstOrDefaultAsync(robot => robot.Id.Equals(id)); }

        public async Task<Robot?> ReadByIsarId(string isarId)
        {
            return await GetRobotsWithSubModels()
                .FirstOrDefaultAsync(robot => robot.IsarId.Equals(isarId));
        }

        public async Task<IEnumerable<string>> ReadAllActivePlants()
        {
            return await GetRobotsWithSubModels().Where(r => r.Enabled && r.CurrentInstallation != null).Select(r => r.CurrentInstallation!.InstallationCode).ToListAsync();
        }

        public async Task<Robot> Update(Robot robot)
        {
            if (robot.CurrentArea is not null) context.Entry(robot.CurrentArea).State = EntityState.Unchanged;

            var entry = context.Update(robot);
            await ApplyDatabaseUpdate(robot.CurrentInstallation);
            _ = signalRService.SendMessageAsync("Robot updated", robot?.CurrentInstallation, robot != null ? new RobotResponse(robot) : null);
            return entry.Entity;
        }

        public async Task<Robot?> Delete(string id)
        {
            var robot = await GetRobotsWithSubModels().FirstOrDefaultAsync(ev => ev.Id.Equals(id));
            if (robot is null) return null;

            context.Robots.Remove(robot);
            await ApplyDatabaseUpdate(robot.CurrentInstallation);
            _ = signalRService.SendMessageAsync("Robot deleted", robot?.CurrentInstallation, robot != null ? new RobotResponse(robot) : null);
            return robot;
        }

        public async Task<IList<Robot>> ReadLocalizedRobotsForInstallation(string installationCode)
        {
            return await GetRobotsWithSubModels()
                .Where(robot =>
#pragma warning disable CA1304
                    robot.CurrentInstallation != null &&
                    robot.CurrentInstallation.InstallationCode.ToLower().Equals(installationCode.ToLower())
#pragma warning restore CA1304
                    && robot.CurrentArea != null)
                .ToListAsync();
        }

        public async Task SetRobotOffline(string robotId)
        {
            var robot = await ReadById(robotId);
            if (robot == null)
            {
                logger.LogError("Robot with ID: {RobotId} was not found in the database", robotId);
                return;
            }

            if (robot.CurrentMissionId != null)
            {
                var missionRun = await missionRunService.ReadById(robot.CurrentMissionId);
                if (missionRun != null)
                {
                    missionRun.SetToFailed();
                    await missionRunService.Update(missionRun);
                    logger.LogWarning(
                        "Mission '{Id}' failed because ISAR could not be reached",
                        missionRun.Id
                    );
                }
            }

            try
            {
                await UpdateRobotStatus(robot.Id, RobotStatus.Offline);
                await UpdateCurrentMissionId(robot.Id, null);
                await UpdateRobotEnabled(robot.Id, false);
                await UpdateCurrentArea(robot.Id, null);
            }
            catch (RobotNotFoundException) { }
        }

        private async Task<Robot> UpdateRobotProperty(string robotId, string propertyName, object? value)
        {
            _robotSemaphore.WaitOne();
            var robot = await ReadById(robotId);
            if (robot is null)
            {
                string errorMessage = $"Robot with ID {robotId} was not found in the database";
                logger.LogError("{Message}", errorMessage);
                _robotSemaphore.Release();
                throw new RobotNotFoundException(errorMessage);
            }

            foreach (var property in typeof(Robot).GetProperties())
            {
                if (property.Name == propertyName)
                {
                    logger.LogInformation("Setting {robotName} field {propertyName} from {oldValue} to {NewValue}", robot.Name, propertyName, property.GetValue(robot), value);
                    property.SetValue(robot, value);
                }
            }

            robot = await Update(robot);
            _robotSemaphore.Release();
            return robot;
        }

        private IQueryable<Robot> GetRobotsWithSubModels()
        {
            var accessibleInstallationCodes = accessRoleService.GetAllowedInstallationCodes();
            return context.Robots
                .Include(r => r.VideoStreams)
                .Include(r => r.Model)
                .Include(r => r.CurrentInstallation)
                .Include(r => r.CurrentArea)
                .ThenInclude(area => area != null ? area.Deck : null)
                .Include(r => r.CurrentArea)
                .ThenInclude(area => area != null ? area.Plant : null)
                .Include(r => r.CurrentArea)
                .ThenInclude(area => area != null ? area.Installation : null)
                .Include(r => r.CurrentArea)
                .ThenInclude(area => area != null ? area.SafePositions : null)
                .Include(r => r.CurrentArea)
                .ThenInclude(area => area != null ? area.Deck : null)
                .ThenInclude(deck => deck != null ? deck.DefaultLocalizationPose : null)
                .ThenInclude(defaultLocalizationPose => defaultLocalizationPose != null ? defaultLocalizationPose.Pose : null)
#pragma warning disable CA1304
                .Where((r) => r.CurrentInstallation == null || r.CurrentInstallation.InstallationCode == null || accessibleInstallationCodes.Result.Contains(r.CurrentInstallation.InstallationCode.ToUpper()));
#pragma warning restore CA1304
        }

        private async Task ApplyDatabaseUpdate(Installation? installation)
        {
            var accessibleInstallationCodes = await accessRoleService.GetAllowedInstallationCodes();
            if (installation == null || accessibleInstallationCodes.Contains(installation.InstallationCode.ToUpper(CultureInfo.CurrentCulture)))
                await context.SaveChangesAsync();
            else
                throw new UnauthorizedAccessException($"User does not have permission to update robot in installation {installation.Name}");
        }

        private async Task VerifyThatUserIsAuthorizedToUpdateDataForInstallation(Installation? installation)
        {
            var accessibleInstallationCodes = await accessRoleService.GetAllowedInstallationCodes();
            if (installation == null || accessibleInstallationCodes.Contains(installation.InstallationCode.ToUpper(CultureInfo.CurrentCulture))) return;

            throw new UnauthorizedAccessException($"User does not have permission to update robot in installation {installation.Name}");
        }

        private void NotifySignalROfUpdatedRobot(Robot robot, Installation installation)
        {
            _ = signalRService.SendMessageAsync("Robot updated", installation, robot != null ? new RobotResponse(robot) : null);
        }
    }
}
