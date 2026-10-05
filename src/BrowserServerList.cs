using System;
using Steamworks;

namespace KellysJOINCHECK
{
    // A browser owns one broad list. Filter changes refresh it in place, avoiding the
    // empty results observed when allocating successive filtered master requests.
    internal sealed class BrowserServerList : IDisposable
    {
        private readonly ISteamMatchmakingServerListResponse response;
        private readonly Action<gameserveritem_t> result;
        private readonly Action<EMatchMakingServerResponse> finished;
        private readonly Action<Exception> failure;
        private HServerListRequest? request;
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
                if(request.HasValue && SteamMatchmakingServers.GetServerCount(request.Value)>0)
                    SteamMatchmakingServers.RefreshQuery(request.Value);
                else
                {
                    // An empty list cannot be repopulated by RefreshQuery; retry discovery
                    // only on the user's next refresh, without a background retry loop.
                    Release();
                    var filters=new[]{new MatchMakingKeyValuePair_t { m_szKey="dedicated",m_szValue="" }};
                    request=SteamMatchmakingServers.RequestInternetServerList(SteamUtils.GetAppID(),filters,1,response);
                    if(request.Value==HServerListRequest.Invalid) throw new InvalidOperationException("Steam server-list request unavailable");
                }
            }
            catch { InProgress=false; throw; }
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
            InProgress=false;
            try { finished(state); } catch(Exception ex) { failure(ex); }
        }
        private void Release()
        {
            if(!request.HasValue) return;
            var owned=request.Value; request=null;
            if(owned==HServerListRequest.Invalid) return;
            SteamMatchmakingServers.CancelQuery(owned); SteamMatchmakingServers.ReleaseRequest(owned);
            GC.KeepAlive(response);
        }
        public void Dispose()
        {
            if(disposed) return; disposed=true;
            try { Release(); } finally { InProgress=false; }
        }
    }
}
