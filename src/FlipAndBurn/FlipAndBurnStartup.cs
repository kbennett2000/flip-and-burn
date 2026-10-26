using UnityEngine;

namespace FlipAndBurn
{
    // Logs once when the main menu first loads, to prove the DLL is installed and running.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class FlipAndBurnStartup : MonoBehaviour
    {
        private void Start()
        {
            Debug.Log("[FlipAndBurn] loaded");
        }
    }
}
