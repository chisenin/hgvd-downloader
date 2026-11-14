"""抓 Avalonia 窗口并按行输出颜色分段，用于客观判断进度条是否渐变。"""
import ctypes, ctypes.wintypes as w, time, sys

user32 = ctypes.windll.user32; gdi32 = ctypes.windll.gdi32
user32.SetProcessDPIAware()

class BIH(ctypes.Structure):
    _fields_=[("biSize",w.DWORD),("biWidth",ctypes.c_long),("biHeight",ctypes.c_long),("biPlanes",w.WORD),
              ("biBitCount",w.WORD),("biCompression",w.DWORD),("biSizeImage",w.DWORD),("biX",ctypes.c_long),
              ("biY",ctypes.c_long),("biClrUsed",w.DWORD),("biClrImp",w.DWORD)]
class BI(ctypes.Structure):
    _fields_=[("h",BIH),("c",w.DWORD*3)]

def grab(hwnd):
    r = w.RECT(); user32.GetWindowRect(hwnd, ctypes.byref(r))
    W,H = r.right-r.left, r.bottom-r.top
    src=user32.GetDC(0); mem=gdi32.CreateCompatibleDC(src); bmp=gdi32.CreateCompatibleBitmap(src,W,H)
    gdi32.SelectObject(mem,bmp); gdi32.BitBlt(mem,0,0,W,H,src,r.left,r.top,0x00CC0020)
    bi=BI(); bi.h.biSize=ctypes.sizeof(BIH); bi.h.biWidth=W; bi.h.biHeight=-H; bi.h.biPlanes=1
    bi.h.biBitCount=32; bi.h.biCompression=0
    buf=ctypes.create_string_buffer(W*H*4); gdi32.GetDIBits(mem,bmp,0,H,buf,ctypes.byref(bi),0)
    raw=bytes((ctypes.c_ubyte*(W*H*4)).from_buffer(buf))
    gdi32.DeleteObject(bmp); gdi32.DeleteDC(mem); user32.ReleaseDC(0,src)
    return raw, W, H

def segments(raw, W, y, x0=0, x1=None):
    """把一行按颜色分段输出（容差 6）"""
    x1 = x1 or W
    out=[]; cur=None
    for x in range(x0,x1):
        i=(y*W+x)*4; c=(raw[i+2],raw[i+1],raw[i])
        if cur and all(abs(c[k]-cur[0][k])<=6 for k in range(3)):
            cur[1]=x
        else:
            if cur: out.append(cur)
            cur=[c,x,x]
    if cur: out.append(cur)
    return [(s[0], s[1], s[2]) for s in out if s[2]-s[1] >= 2]

if __name__ == "__main__":
    hwnd=int(sys.argv[1]); ys=[int(v) for v in sys.argv[2].split(",")]
    x0=int(sys.argv[3]) if len(sys.argv)>3 else 0
    x1=int(sys.argv[4]) if len(sys.argv)>4 else None
    user32.ShowWindow(hwnd,9); user32.SetWindowPos(hwnd,-1,40,30,1500,860,0x0004); time.sleep(1.2)
    raw,W,H = grab(hwnd)
    print("窗口 %dx%d" % (W,H))
    for y in ys:
        print("--- y=%d ---" % y)
        for c,a,b in segments(raw,W,y,x0,x1):
            print("   x=%4d..%4d  RGB=%s" % (a,b,c))
