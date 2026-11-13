using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using NuclearOption.Networking.Lobbies;

namespace KellysJOINCHECK
{
    public sealed partial class ClientPlugin
    {
        private GameplayFingerprintInventory? gameplayInventory;
        private readonly ConditionalWeakTable<LobbyInstance,RemoteGameplayFiles> gameplayRemote = new ConditionalWeakTable<LobbyInstance,RemoteGameplayFiles>();
        private sealed class RemoteGameplayFiles
        {
            internal string Wire="",Header="",Marker="";
            internal GameplayFingerprintSnapshot? Value;
        }
        private void StartGameplayChecks()
        {
            gameplayInventory=new GameplayFingerprintInventory(this,message=>Logger.LogWarning(message),GameplayFilesChanged);
            gameplayInventory.Start(harmony!);
            if(mods?.ContentLoadingFinished==true)gameplayInventory.NotifyLoaderCompleted();
        }
        private void GameplayFilesChanged()
        {
            Guard(()=> { if(previewLobby!=null&&preview)RefreshPreview();else if(lobby!=null&&visible)RefreshReport();RefreshServerActions(); });
        }
        internal void RefreshGameplayFiles()=>gameplayInventory?.RequestRefresh();
        internal void NotifyGameplayLoaderCompleted()=>gameplayInventory?.NotifyLoaderCompleted();
        internal string GameplayHeader(LobbyInstance target)=>Read(target,"gfpv")+"|"+Read(target,GameplayFingerprintProtocol.Header);
        internal bool GameplayCheckSettled=>gameplayInventory?.CurrentWire==Compatibility.Wire;
        internal GameplayFingerprintSnapshot LocalGameplayFiles=>gameplayInventory?.ForWire(Compatibility.Wire)
            ??GameplayFingerprintSnapshot.Unavailable("Local gameplay files have not been checked yet.");
        internal bool RequiresGameplayFiles(LobbyInstance target)=>Read(target,"gfpv").Length>0||Read(target,GameplayFingerprintProtocol.Header).Length>0;
        internal GameplayFingerprintSnapshot? GameplayFilesFor(LobbyInstance target)
        {
            string wire=target.HostVersion,header=Read(target,GameplayFingerprintProtocol.Header),marker=Read(target,"gfpv");
            var cached=gameplayRemote.GetOrCreateValue(target);
            if(cached.Value!=null&&cached.Wire==wire&&cached.Header==header&&cached.Marker==marker)return cached.Value;
            cached.Wire=wire;cached.Header=header;cached.Marker=marker;
            cached.Value=marker.Length>0&&marker!=GameplayFingerprintPolicy.Scope?null:
                GameplayFingerprintProtocol.Decode(key=>Read(target,key),wire);
            return cached.Value;
        }
        internal GameplayFingerprintComparison CompareGameplayFiles(LobbyInstance target)
            =>GameplayFingerprintComparison.Compare(GameplayFilesFor(target),LocalGameplayFiles);
        internal bool GameplayAllowsJoin(LobbyInstance target)=>!RequiresGameplayFiles(target)||CompareGameplayFiles(target).Verified;
        internal string GameplayReport(LobbyInstance target)
        {
            var output=new StringBuilder("\nExact gameplay file check (selected content and audited gameplay DLLs):\n");
            if(!RequiresGameplayFiles(target))return output.Append("Unavailable on this server. Its game/version checks still apply; exact files have not been verified.\n").ToString();
            var compared=CompareGameplayFiles(target);
            if(!compared.Available)output.Append("Not verified: ").Append(compared.Error).Append('\n');
            else if(compared.Verified)output.Append("Exact files match within ").Append(GameplayFingerprintPolicy.Scope).Append(". File identity does not certify asset loading or runtime behavior. Raw game compatibility and authentication still apply.\n");
            foreach(var difference in compared.Differences)
                output.Append(difference.Type).Append(": ").Append(difference.Kind).Append(' ').Append(difference.Id)
                    .Append("; server version=").Append(difference.ServerVersion).Append(" SHA256=").Append(difference.ServerHash)
                    .Append("; local version=").Append(difference.LocalVersion).Append(" SHA256=").Append(difference.LocalHash).Append('\n');
            var remote=GameplayFilesFor(target);var local=LocalGameplayFiles;
            if(remote!=null)output.Append("  Server unresolved asset patches=").Append(remote.PendingAssetPatches).Append('\n');
            output.Append("  Local unresolved asset patches=").Append(local.PendingAssetPatches).Append('\n');
            if(remote?.PendingAssetPatches>0||local.PendingAssetPatches>0)
                output.Append("  Loading health warning only: pending asset patches were not classified, removed or repaired. Exact file checks do not bypass the game's own join checks.\n");
            if(remote!=null&&remote.ContentSetHash.Length>0)output.Append("  Server selected-content SHA256=").Append(remote.ContentSetHash).Append("; compact exact check cannot identify which same-version bundle differs.\n");
            if(remote!=null)foreach(var file in remote.Records)output.Append("  Server ").Append(file.Kind).Append(' ').Append(file.Id).Append(' ').Append(file.Version).Append(" SHA256=").Append(file.Hash).Append('\n');
            if(local.Ready&&local.Complete)foreach(var file in local.Records)output.Append("  Loaded ").Append(file.Kind).Append(' ').Append(file.Id).Append(' ').Append(file.Version).Append(" SHA256=").Append(file.Hash).Append('\n');
            return output.ToString();
        }
    }
}
