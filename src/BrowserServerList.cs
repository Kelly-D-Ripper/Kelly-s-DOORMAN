using System;
using Steamworks;

namespace KellysJOINCHECK
{
    // Discover the master list on each search so recently started servers appear.
    // Keep the previous request until discovery finishes: Steam may occasionally
    // return an empty list, in which case it can still refresh the known servers.
    internal sealed class BrowserServerList : IDisposable
    {
        private readonly ISteamMatchmakingServerListResponse response;
        private readonly Action<gameserveritem_t> result;
        private readonly Action<EMatchMakingServerResponse> finished;
        private readonly Action<Exception> failure;
        private HServerListRequest? request, previous;
        private bool disposed;
        internal bool InProgress { get; private set; }
        internal BrowserServerList(Action<gameserveritem_t> result,Action<EMatchMakingServerResponse> finished,Action<Exception> failure)
        {
            this.result=result; this.finished=finished; this.failure=failure;
            response=new ISteamMatchmakingServerListResponse(Responded,(handle,index)=>{},Completed);
        }
        internal void Start()
        {
            if(disposed) throw new ObjectDisposedException(nameof(BrowserServerList));
            if(InProgress) throw new InvalidOperationException("Server search already running");
            InProgress=true;
            try
            {
                ReleasePrevious();
                if(request.HasValue && SteamMatchmakingServers.GetServerCount(request.Value)>0) { previous=request;request=null; }
                else Release();
                var filters=new[]{new MatchMakingKeyValuePair_t { m_szKey="dedicated",m_szValue="" }};
                request=SteamMatchmakingServers.RequestInternetServerList(SteamUtils.GetAppID(),filters,1,response);
                if(request.Value==HServerListRequest.Invalid) throw new InvalidOperationException("Steam server-list request unavailable");
            }
            catch { Release();request=previous;previous=null;InProgress=false;throw; }
        }
        private bool Owns(HServerListRequest handle) => !disposed && request.HasValue && request.Value==handle;
        private void Responded(HServerListRequest handle,int index)
        {
            if(!Owns(handle) || !InProgress) return;
            try { result(SteamMatchmakingServers.GetServerDetails(handle,index)); }
            catch(Exception ex) { failure(ex); }
        }
        private void Completed(HServerListRequest handle,EMatchMakingServerResponse state)
        {
            if(!Owns(handle) || !InProgress) return;
            if(previous.HasValue && (state==EMatchMakingServerResponse.eNoServersListedOnMasterServer || SteamMatchmakingServers.GetServerCount(handle)==0))
            {
                Release();request=previous;previous=null;
                try { SteamMatchmakingServers.RefreshQuery(request.Value);return; }
                catch(Exception ex) { InProgress=false;failure(ex); }
            }
            ReleasePrevious();
            InProgress=false;
            try { finished(state); } catch(Exception ex) { failure(ex); }
        }
        private void Release()
        {
            if(!request.HasValue) return;
            var owned=request.Value; request=null;ReleaseHandle(owned);
        }
        private void ReleasePrevious()
        {
            if(!previous.HasValue)return;
            var owned=previous.Value;previous=null;ReleaseHandle(owned);
        }
        private void ReleaseHandle(HServerListRequest owned)
        {
            if(owned==HServerListRequest.Invalid) return;
            SteamMatchmakingServers.CancelQuery(owned); SteamMatchmakingServers.ReleaseRequest(owned);
            GC.KeepAlive(response);
        }
        public void Dispose()
        {
            if(disposed) return; disposed=true;
            try { try { Release(); } finally { ReleasePrevious(); } } finally { InProgress=false; }
        }
    }
}
