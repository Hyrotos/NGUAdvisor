using UnityEngine;
using Object = UnityEngine.Object;

namespace NGUAdvisor
{
    public class Loader
    {
        private static GameObject _load;
        private static Main _reference;

        // WHY A NAMED HOST OBJECT IS THE "ALREADY RUNNING" SIGNAL. A static cannot be one: a second
        // injection loads a second copy of this assembly whose statics start fresh, so Init would
        // have no idea the first Main is running. Two Mains means two update loops, two allocation
        // passes and two file watchers over one save. GameObject.Find matches by NAME across
        // assemblies, which survives that. It only finds ACTIVE objects, which is what lets a hot
        // reload through: Unload deactivates the old host before the new payload's Init runs.
        private const string HostName = "NGUAdvisorHost";

        public static void Init()
        {
            if (GameObject.Find(HostName) != null)
            {
                Debug.LogWarning("NGUAdvisor: already running in this game session — not starting a second "
                    + "instance. Use Hot-reload advisor (F5) to load a new build, or restart the game.");
                return;
            }
            _load = new GameObject(HostName);
            _reference = _load.AddComponent<Main>();
            Object.DontDestroyOnLoad(_load);
        }

        public static void Unload()
        {
            _reference.Unload();
            _load.SetActive(false);
            Object.Destroy(_load);
        }
    }
}
