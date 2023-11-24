﻿using Api.Database.Context;
using Api.Services;
namespace Api.EventHandlers
{
    public class InspectionFindingEventHandler(IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<InspectionFindingEventHandler> logger) : BackgroundService
    {
        private readonly TimeSpan _interval = configuration.GetValue<TimeSpan>("InspectionFindingEventHandler:Interval");
        private IInspectionFindingService InspectionFindingService => scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IInspectionFindingService>();
        private readonly TimeSpan _timeSpan = configuration.GetValue<TimeSpan>("InspectionFindingEventHandler:TimeSpan");

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {


            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, stoppingToken);

                    using var scope = scopeFactory.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<FlotillaDbContext>();
                    var inspectionFindings = await InspectionFindingService.RetrieveInspectionFindings(_timeSpan, context);

                    logger.LogInformation("Found {count} inspection findings in the last {interval}.", inspectionFindings.Count, _timeSpan);

                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }

    }
}