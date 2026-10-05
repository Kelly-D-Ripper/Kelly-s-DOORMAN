namespace KellysJOINCHECK
{
    internal static class BrowserPresentation
    {
        internal const float ToolbarHeight = 64;
        internal const float ReservedHeight = 72;
        internal static bool UseFavouriteFilter(bool requested,int savedCount) => requested && savedCount>0;
    }
}
