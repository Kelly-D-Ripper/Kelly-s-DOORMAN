using UnityEngine;
using UnityEngine.SceneManagement;

namespace KellysJOINCHECK
{
    // The game may lock its cursor after a join error or when focus returns.
    // Borrow it only while the checklist is open, and never while unfocused.
    internal sealed class ChecklistCursor
    {
        private bool open, owned, oldVisible;
        private CursorLockMode oldLock;
        private int oldScene;

        internal void Open()
        {
            if (open) return;
            oldVisible = Cursor.visible;
            oldLock = Cursor.lockState;
            oldScene = SceneManager.GetActiveScene().handle;
            owned = !oldVisible || oldLock != CursorLockMode.None;
            open = true;
            KeepAvailable();
        }

        internal void KeepAvailable()
        {
            if (!open || !Application.isFocused) return;
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
        }

        internal void Close(bool anotherMenuOpen)
        {
            if (!open) return;
            open = false;
            if (owned && Application.isFocused && !anotherMenuOpen && Cursor.visible && Cursor.lockState == CursorLockMode.None
                && SceneManager.GetActiveScene().handle == oldScene)
            { Cursor.lockState = oldLock; Cursor.visible = oldVisible; }
            owned = false;
        }
    }
}
