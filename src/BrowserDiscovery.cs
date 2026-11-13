using System;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using NuclearOption.SavedMission;
using Steamworks;
using UnityEngine;

namespace KellysJOINCHECK
{
    // Feed discoveries through the game's existing rule queries, ping filters and rows.
    // This adapter is entered only by LobbyList.GetListOfLobbies, never by direct joins.
    internal sealed class BrowserDiscovery : IDisposable
    {
        private readonly object controller;
        private readonly SteamLobby manager;
        private readonly Action<bool> finished;
        private readonly BrowserServerList servers;
        private readonly MethodInfo updated,complete,cancel,setProgress,setNumber,getNumber;
        private readonly MethodInfo pending;
        private readonly bool updatedUsesFilterArgument;
        private LobbySearchFilter filter;
        private string version="";
        private string? mission;
        private bool disposed;
        internal static Type ControllerType => AccessTools.Inner(typeof(SteamLobby),"ServerListRequest") ?? throw new MissingMemberException("ServerListRequest");
        private static MethodInfo Required(Type type,string name) => AccessTools.Method(type,name) ?? throw new MissingMethodException(type.FullName,name);
        internal static void ValidateContract()
        {
            foreach(var method in new[]{"RequestServerLobbies","OnServerUpdated","OnRefreshComplete","Cancel","get_InProgress","set_InProgress","get_searchNumber","set_searchNumber"}) Required(ControllerType,method);
            var args=Required(ControllerType,"OnServerUpdated").GetParameters();
            if(args.Length!=1 && args.Length!=2 || args[0].ParameterType!=typeof(gameserveritem_t) || args.Length==2 && args[1].ParameterType!=typeof(LobbySearchFilter))
                throw new NotSupportedException("Unsupported OnServerUpdated contract");
            if(args.Length==1 && AccessTools.Field(ControllerType,"filter")?.FieldType!=typeof(LobbySearchFilter))
                throw new NotSupportedException("Updated browser filter field is unavailable");
            Required(typeof(SteamLobby),"CheckPendingRefresh"); Required(typeof(SteamLobby),"get_serverLobbiesRefreshInProgress");
            if(AccessTools.Field(typeof(SteamLobby),"playerLobbyRefreshInProgress")==null) throw new MissingFieldException("playerLobbyRefreshInProgress");
        }
        internal BrowserDiscovery(object controller,Action<string> warn,Action<bool> finished)
        {
            this.controller=controller; this.finished=finished; manager=SteamLobby.instance;
            var type=controller.GetType(); updated=Required(type,"OnServerUpdated"); complete=Required(type,"OnRefreshComplete"); cancel=Required(type,"Cancel");
            updatedUsesFilterArgument=updated.GetParameters().Length==2;
            setProgress=Required(type,"set_InProgress"); setNumber=Required(type,"set_searchNumber"); getNumber=Required(type,"get_searchNumber");
            pending=Required(typeof(SteamLobby),"CheckPendingRefresh");
            servers=new BrowserServerList(Result,Finished,ex=>warn("Dedicated browser callback failed: "+ex.GetType().Name));
        }
        internal bool Owns(object value) => ReferenceEquals(controller,value);
        internal void Start(LobbySearchFilter value)
        {
            filter=value; version=Application.version;
            mission=(uint)(value.MissionPvpType-1)<=1u ? MissionTag.GetPvpTypeLobbyString(value.MissionPvpType) : null;
            cancel.Invoke(controller,null);
            if(!updatedUsesFilterArgument) (AccessTools.Field(controller.GetType(),"filter") ?? throw new MissingFieldException("filter")).SetValue(controller,value);
            setNumber.Invoke(controller,new object[]{(int)getNumber.Invoke(controller,null)+1});
            setProgress.Invoke(controller,new object[]{true});
            try { servers.Start(); }
            catch { setProgress.Invoke(controller,new object[]{false}); throw; }
        }
        private void Result(gameserveritem_t details)
        {
            if(details!=null && BrowserServerFilter.Matches(details.GetGameTags(),filter.ignoreVersionFilter,version,mission))
                updated.Invoke(controller,updatedUsesFilterArgument ? new object[]{details,filter} : new object[]{details});
        }
        private void Finished(EMatchMakingServerResponse state)
        {
            setProgress.Invoke(controller,new object[]{false});
            complete.Invoke(controller,new object[]{HServerListRequest.Invalid,state});
            finished(state==EMatchMakingServerResponse.eNoServersListedOnMasterServer);
            pending.Invoke(manager,null);
        }
        public void Dispose()
        {
            if(disposed) return; disposed=true;
            try { servers.Dispose(); }
            finally { cancel.Invoke(controller,null); setProgress.Invoke(controller,new object[]{false}); }
        }
    }
}
