﻿using Account;
using Dalamud.Logging;
using Pal.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Pal.Client.Scheduled
{
    internal class QueuedImport : IQueueOnFrameworkThread
    {
        private readonly ExportRoot _export;
        private Guid _exportId;
        private int importedTraps;
        private int importedHoardCoffers;

        public QueuedImport(string sourcePath)
        {
            using (var input = File.OpenRead(sourcePath))
                _export = ExportRoot.Parser.ParseFrom(input);
        }

        public void Run(Plugin plugin, ref bool recreateLayout, ref bool saveMarkers)
        {
            try
            {
                if (!Validate())
                    return;

                var config = Service.Configuration;
                var oldExportIds = string.IsNullOrEmpty(_export.ServerUrl) ? config.ImportHistory.Where(x => x.RemoteUrl == _export.ServerUrl).Select(x => x.Id).Where(x => x != Guid.Empty).ToList() : new List<Guid>();

                foreach (var remoteFloor in _export.Floors)
                {
                    ushort territoryType = (ushort)remoteFloor.TerritoryType;
                    var localState = plugin.GetFloorMarkers(territoryType);

                    CleanupFloor(localState, oldExportIds);
                    ImportFloor(remoteFloor, localState);

                    localState.Save();
                }

                config.ImportHistory.RemoveAll(hist => oldExportIds.Contains(hist.Id) || hist.Id == _exportId);
                config.ImportHistory.Add(new Configuration.ImportHistoryEntry
                {
                    Id = _exportId,
                    RemoteUrl = _export.ServerUrl,
                    ExportedAt = _export.CreatedAt.ToDateTime(),
                    ImportedAt = DateTime.UtcNow,
                });
                config.Save();

                recreateLayout = true;
                saveMarkers = true;

                Service.Chat.Print($"Imported {importedTraps} new trap locations and {importedHoardCoffers} new hoard coffer locations.");
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "Import failed");
                Service.Chat.PrintError($"Import failed: {e}");
            }
        }

        private bool Validate()
        {
            if (_export.ExportVersion != ExportConfig.ExportVersion)
            {
                Service.Chat.PrintError("Import failed: Incompatible version.");
                return false;
            }

            if (!Guid.TryParse(_export.ExportId, out _exportId) || _exportId == Guid.Empty)
            {
                Service.Chat.PrintError("Import failed: No id present.");
                return false;
            }

            if (string.IsNullOrEmpty(_export.ServerUrl))
            {
                // If we allow for backups as import/export, this should be removed
                Service.Chat.PrintError("Import failed: Unknown server.");
                return false;
            }

            return true;
        }

        private void CleanupFloor(LocalState localState, List<Guid> oldExportIds)
        {
            // When saving a floor state, any markers not seen, not remote seen, and not having an import id are removed;
            // so it is possible to remove "wrong" markers by not having them be in the current import.
            foreach (var marker in localState.Markers)
                marker.Imports.RemoveAll(id => oldExportIds.Contains(id));
        }

        private void ImportFloor(ExportFloor remoteFloor, LocalState localState)
        {
            var remoteMarkers = remoteFloor.Objects.Select(m => new Marker((Marker.EType)m.Type, new Vector3(m.X, m.Y, m.Z)) { WasImported = true });
            foreach (var remoteMarker in remoteMarkers)
            {
                Marker? localMarker = localState.Markers.SingleOrDefault(x => x == remoteMarker);
                if (localMarker == null)
                {
                    localState.Markers.Add(remoteMarker);
                    localMarker = remoteMarker;

                    if (localMarker.Type == Marker.EType.Trap)
                        importedTraps++;
                    else if (localMarker.Type == Marker.EType.Hoard)
                        importedHoardCoffers++;
                }

                remoteMarker.Imports.Add(_exportId);
            }
        }
    }
}
