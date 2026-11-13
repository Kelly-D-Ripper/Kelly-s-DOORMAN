using System;
using System.Collections.Generic;
using System.Linq;

namespace KellysJOINCHECK
{
    // Download records are only suggestions until the exact server fingerprint
    // binds their content IDs, versions and physical file hashes together.
    internal static class ServerDownloadProof
    {
        internal static bool Matches(GameplayFingerprintSnapshot remote,GameplayFingerprintSnapshot available,
            IReadOnlyDictionary<string,string> downloaded,out string error)
        {
            error="";
            if(!GameplayFingerprintPolicy.Normalize(remote,out var wanted,out error)||!wanted.Ready||!wanted.Complete)
            {error="The server's exact mod fingerprints are unavailable.";return false;}
            if(!GameplayFingerprintPolicy.Normalize(available,out var present,out error)||!present.Ready||!present.Complete||present.ContentSetHash.Length>0)
            {error="The installed mod fingerprints are unavailable.";return false;}
            var selected=GameplayFingerprintPolicy.SelectedContent(wanted);
            var selectedIds=new HashSet<string>(selected.Select(r=>r.Id),StringComparer.Ordinal);
            if(downloaded.Keys.Any(id=>!selectedIds.Contains(id)))
            {error="A download is not part of the server's verified mod list.";return false;}
            var actual=present.Records.Where(r=>r.Kind=="content").ToDictionary(r=>r.Id,StringComparer.Ordinal);
            var expected=wanted.Records.Where(r=>r.Kind=="content").ToDictionary(r=>r.Id,StringComparer.Ordinal);
            var built=new List<GameplayFileRecord>();
            foreach(var required in selected)
            {
                string hash="";
                if(downloaded.TryGetValue(required.Id,out var newHash))hash=newHash;
                else if(actual.TryGetValue(required.Id,out var old)&&old.Version==required.Version)hash=old.Hash;
                if(!GameplayFingerprintPolicy.FullHash(hash))
                {error="Cannot verify "+Diagnostics.Clean(required.Id)+" "+Diagnostics.Clean(required.Version)+". Ask the host for the exact file.";return false;}
                if(expected.TryGetValue(required.Id,out var exact)&&!string.Equals(exact.Hash,hash,StringComparison.OrdinalIgnoreCase))
                {error="The downloaded file for "+Diagnostics.Clean(required.Id)+" differs from the server. Ask the host for its exact file.";return false;}
                built.Add(new GameplayFileRecord {Kind="content",Id=required.Id,Version=required.Version,Hash=hash});
            }
            if(wanted.ContentSetHash.Length>0&&!GameplayFingerprintPolicy.ContentHash(built).Equals(wanted.ContentSetHash,StringComparison.OrdinalIgnoreCase))
            {error="The downloaded mod files do not match the server's exact set. Ask the host for its mod files.";return false;}
            return true;
        }
    }
}
