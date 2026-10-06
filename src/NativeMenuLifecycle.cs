using System.Collections;
using System.Linq;
using BepInEx;
using NuclearOption.SceneLoading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private bool waitingMenu;
        private static bool InMenuScene(Scene scene,string loadingKey)=>MenuScene.Matches(scene.name,scene.path,loadingKey);
        private void BeginMenuAttachment()
        {if(waitingMenu)return;waitingMenu=true;owner.StartCoroutine(AttachMenuWhenReady());}
        private IEnumerator AttachMenuWhenReady()
        {
            log("Mods attachment wait started: scene="+SceneManager.GetActiveScene().path+"; native="+MainMenu.State+"; contentReady="+loader.LoadingFinished);
            var gate=new StartupReadyGate();float until=Time.unscaledTime+300;
            while(Time.unscaledTime<until&&!gate.Check(Time.unscaledTime,MainMenu.State==MainMenu.LoadingState.Loaded,loader.LoadingFinished,InMenuScene(SceneManager.GetActiveScene(),MapLoader.MainMenu)&&UnityEngine.EventSystems.EventSystem.current!=null&&UnityEngine.EventSystems.EventSystem.current.isActiveAndEnabled&&MenuSafety().Length==0))yield return new WaitForSecondsRealtime(.25f);
            waitingMenu=false;
            if(Time.unscaledTime>=until){log("Mods menu is still waiting for completed startup: scene="+SceneManager.GetActiveScene().path+", native="+MainMenu.State+", contentReady="+loader.LoadingFinished+", safety="+MenuSafety()+". Returning to Main Menu will retry.");yield break;}
            foreach(var menu in Resources.FindObjectsOfTypeAll<MainMenu>().Where(m=>m&&m.isActiveAndEnabled&&m.gameObject.scene.IsValid()))
                try{Attach(menu);}catch(System.Exception ex){log("Mods menu button unavailable after loading: "+ex.Message);}
            RestartStatus.Write(Paths.CachePath,"ready");
        }
        private IEnumerator AttachBrowserAfterFrame(int handle)
        {
            // A scene event is independent of Unity's cached Start invocation.
            // Run once after native Start has initialized the browser fields.
            yield return null;
            if(SceneManager.GetActiveScene().handle!=handle){log("Browser attachment skipped because the active scene changed: "+SceneManager.GetActiveScene().path);yield break;}
            BrowserHooks.EnsureControls();
        }
    }
}
