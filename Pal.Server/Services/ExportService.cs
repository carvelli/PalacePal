using Account;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Pal.Common;
using System.Data;
using static Account.ExportService;

namespace Pal.Server.Services
{
    internal class ExportService : ExportServiceBase
    {
        private readonly ILogger<ExportService> _logger;
        private readonly PalContext _dbContext;

        public ExportService(ILogger<ExportService> logger, PalContext dbContext)
        {
            _logger = logger;
            _dbContext = dbContext;
        }

        [Authorize(Roles = "export:run")]
        public override async Task<ExportReply> Export(ExportRequest request, ServerCallContext context)
        {
            try
            {
                var objectsByFloor = await _dbContext.Locations.Where(x => x.SeenLocations.Count >= 10)
                    .GroupBy(x => x.TerritoryType)
                    .ToDictionaryAsync(x => x.Key, x => x.ToList(), context.CancellationToken);

                var exportRoot = new ExportRoot
                {

                    ExportId = Guid.NewGuid().ToString(),
                    ExportVersion = ExportConfig.ExportVersion,
                    ServerUrl = request.ServerUrl,
                    CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),

                };

                foreach (var (territoryType, objects) in objectsByFloor)
                {
                    var floorReply = new ExportFloor { TerritoryType = territoryType };
                    floorReply.Objects.AddRange(objects.Select(o => new ExportObject
                    {
                        Type = (ExportObjectType)o.Type,
                        X = o.X,
                        Y = o.Y,
                        Z = o.Z,
                    }));

                    exportRoot.Floors.Add(floorReply);
                }

                return new ExportReply
                {
                    Success = true,
                    Data = exportRoot
                };
            }
            catch (Exception e)
            {
                _logger.LogError("Could not create export: {e}", e);
                return new ExportReply { Success = false, Error = ExportError.Unknown };
            }
        }
    }
}
