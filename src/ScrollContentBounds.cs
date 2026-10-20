using System;
using System.Runtime.InteropServices;
using D=System.Drawing;

namespace QingCapture;

// This is only a native scroll-child boundary, not inferred document structure.
// Custom headers, composers or floating controls inside that child remain unknown.
internal static class ScrollContentBounds
{
    internal static D.Rectangle? ForTarget(IntPtr target,IntPtr root,D.Rectangle lockedColumn)
    {
        if(target==IntPtr.Zero||root==IntPtr.Zero||target==root||lockedColumn.Width<=0||lockedColumn.Height<=0)return null;
        if(Native.GetAncestor(target,2)!=root||!Native.IsWindowVisible(target)||Native.IsIconic(root))return null;
        // A range on an ancestor could describe another pane. Require the exact
        // delivery child to own an enabled, consistent native vertical range.
        if(!Native.ReadVerticalScrollEnd(target).HasValue)return null;
        if(!ClientBounds(target,out var visible))return null;

        bool reachedRoot=false;
        IntPtr window=GetParent(target);
        // Intersect every ancestor's visible client, since an oversized child can
        // otherwise extend below its actual pane. Bound traversal and never send
        // messages to a source window or query an accessibility provider here.
        for(int depth=0;depth<32&&window!=IntPtr.Zero;depth++) {
            if(!Native.IsWindowVisible(window)||!ClientBounds(window,out var bounds))return null;
            visible=D.Rectangle.Intersect(visible,bounds);
            if(visible.IsEmpty)return null;
            if(window==root){reachedRoot=true;break;}
            var parent=GetParent(window);if(parent==window)return null;window=parent;
        }
        if(!reachedRoot||Native.GetAncestor(target,2)!=root)return null;

        // Do not silently narrow a user's locked column, even when a scrollbar or
        // another pane clips its right edge. The caller must keep its fallback or
        // explain that the chosen width is not wholly inside this native viewport.
        long right=(long)lockedColumn.X+lockedColumn.Width;
        if(lockedColumn.Left<visible.Left||right>visible.Right)return null;
        int top=Math.Max(lockedColumn.Top,visible.Top);
        if(top>=visible.Bottom)return null;
        return new D.Rectangle(lockedColumn.Left,top,lockedColumn.Width,visible.Bottom-top);
    }

    private static bool ClientBounds(IntPtr window,out D.Rectangle bounds)
    {
        bounds=D.Rectangle.Empty;
        if(!Native.GetClientRect(window,out var client)||client.Right<=client.Left||client.Bottom<=client.Top)return false;
        var topLeft=new Native.Point {X=client.Left,Y=client.Top};
        var bottomRight=new Native.Point {X=client.Right,Y=client.Bottom};
        if(!Native.ClientToScreen(window,ref topLeft)||!Native.ClientToScreen(window,ref bottomRight))return false;
        if(bottomRight.X<=topLeft.X||bottomRight.Y<=topLeft.Y)return false;
        bounds=D.Rectangle.FromLTRB(topLeft.X,topLeft.Y,bottomRight.X,bottomRight.Y);return true;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
}
