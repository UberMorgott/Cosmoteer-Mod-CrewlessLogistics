//go:build windows

package main

import (
	"fmt"
	"runtime"
	"syscall"
	"time"
	"unsafe"
)

// Flat-API export names verified against the export table of
// Cosmoteer\Bin\steam_api64.dll (10.24.16.75).
const (
	fnUGCAccessor     = "SteamAPI_SteamUGC_v021"
	fnUtilsAccessor   = "SteamAPI_SteamUtils_v010"
	fnUserAccessor    = "SteamAPI_SteamUser_v023"
	fnFriendsAccessor = "SteamAPI_SteamFriends_v018"
)

// Steamworks constants (Steamworks SDK headers, mirrored in Steamworks.NET autogen).
const (
	kISteamUGCCallbacks                        = 3400
	kCreateItemResult                          = kISteamUGCCallbacks + 3
	kSubmitItemUpdateResult                    = kISteamUGCCallbacks + 4
	kSteamUGCQueryCompleted                    = kISteamUGCCallbacks + 1
	kAddUGCDependencyResult                    = kISteamUGCCallbacks + 12
	kEItemPreviewTypeImage                     = 0
	kCchPublishedDocumentTitleMax              = 128 + 1
	kCchPublishedDocumentDescriptionMax        = 8000
	kEWorkshopFileTypeCommunity                = 0
	kEResultOK                                 = 1
	kESteamAPIInitResultOK                     = 0
	invalidUGCUpdateHandle              uint64 = 0xFFFFFFFFFFFFFFFF
)

// CreateItemResult_t, Pack=8 on Windows x64.
type createItemResult struct {
	Result         int32
	_              [4]byte
	FileID         uint64
	NeedsAgreement bool
	_              [7]byte
}

// SubmitItemUpdateResult_t, Pack=8 on Windows x64.
type submitItemUpdateResult struct {
	Result         int32
	NeedsAgreement bool
	_              [3]byte
	FileID         uint64
}

// AddUGCDependencyResult_t, Pack=8 (offsets 0/8/16 per Cosmoteer Bin\Steamworks.NET.dll).
type addUGCDependencyResult struct {
	Result  int32
	_       [4]byte
	FileID  uint64
	ChildID uint64
}

// SteamUGCQueryCompleted_t, Pack=8: handle@0 result@8 num@12 total@16 cached@20 cursor[256]@21, size 280.
type steamUGCQueryCompleted struct {
	Handle     uint64
	Result     int32
	NumResults uint32
	Total      uint32
	Cached     bool
	NextCursor [256]byte
	_          [3]byte
}

type steam struct {
	dll                       *syscall.DLL
	ugc, utils, user, friends uintptr
	procs                     map[string]*syscall.Proc
}

func loadSteam(path string) (*steam, error) {
	dll, err := syscall.LoadDLL(path)
	if err != nil {
		return nil, fmt.Errorf("load %s: %w", path, err)
	}
	return &steam{dll: dll, procs: map[string]*syscall.Proc{}}, nil
}

func (s *steam) call(name string, args ...uintptr) uintptr {
	p, ok := s.procs[name]
	if !ok {
		var err error
		if p, err = s.dll.FindProc(name); err != nil {
			panic(fmt.Sprintf("steam_api64.dll lacks export %s: %v", name, err))
		}
		s.procs[name] = p
	}
	r, _, _ := syscall.SyscallN(p.Addr(), args...)
	return r
}

func cbool(r uintptr) bool { return byte(r) != 0 }

func cstr(s string) *byte {
	p, err := syscall.BytePtrFromString(s)
	if err != nil {
		panic(fmt.Sprintf("string contains NUL: %q", s))
	}
	return p
}

func gostr(p uintptr) string {
	if p == 0 {
		return ""
	}
	// Reinterpret the C pointer (owned by steam_api, not the Go heap).
	base := *(*unsafe.Pointer)(unsafe.Pointer(&p))
	n := 0
	for *(*byte)(unsafe.Add(base, n)) != 0 {
		n++
	}
	return string(unsafe.Slice((*byte)(base), n))
}

func (s *steam) init() error {
	var errMsg [1024]byte // SteamErrMsg
	r := s.call("SteamAPI_InitFlat", uintptr(unsafe.Pointer(&errMsg[0])))
	if int32(r) != kESteamAPIInitResultOK {
		return fmt.Errorf("SteamAPI_InitFlat result %d: %s", int32(r), gostr(uintptr(unsafe.Pointer(&errMsg[0]))))
	}
	s.ugc = s.call(fnUGCAccessor)
	s.utils = s.call(fnUtilsAccessor)
	s.user = s.call(fnUserAccessor)
	s.friends = s.call(fnFriendsAccessor)
	if s.ugc == 0 || s.utils == 0 || s.user == 0 || s.friends == 0 {
		return fmt.Errorf("null interface pointer (ugc=%x utils=%x user=%x friends=%x)", s.ugc, s.utils, s.user, s.friends)
	}
	return nil
}

func (s *steam) shutdown() { s.call("SteamAPI_Shutdown") }

func (s *steam) loggedOn() bool  { return cbool(s.call("SteamAPI_ISteamUser_BLoggedOn", s.user)) }
func (s *steam) steamID() uint64 { return uint64(s.call("SteamAPI_ISteamUser_GetSteamID", s.user)) }
func (s *steam) persona() string {
	return gostr(s.call("SteamAPI_ISteamFriends_GetPersonaName", s.friends))
}

// waitCall pumps callbacks until the async call completes, then copies its result into out.
func (s *steam) waitCall(call uint64, out unsafe.Pointer, size uintptr, callbackID int, progress func()) error {
	if call == 0 {
		return fmt.Errorf("invalid SteamAPICall_t")
	}
	deadline := time.Now().Add(30 * time.Minute)
	for {
		s.call("SteamAPI_RunCallbacks")
		var failed bool
		if cbool(s.call("SteamAPI_ISteamUtils_IsAPICallCompleted", s.utils, uintptr(call), uintptr(unsafe.Pointer(&failed)))) {
			if failed {
				return fmt.Errorf("API call failed, reason %d", int32(s.call("SteamAPI_ISteamUtils_GetAPICallFailureReason", s.utils, uintptr(call))))
			}
			break
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("timed out waiting for API call")
		}
		if progress != nil {
			progress()
		}
		time.Sleep(100 * time.Millisecond)
	}
	var failed bool
	ok := cbool(s.call("SteamAPI_ISteamUtils_GetAPICallResult", s.utils, uintptr(call), uintptr(out), size, uintptr(callbackID), uintptr(unsafe.Pointer(&failed))))
	if !ok || failed {
		return fmt.Errorf("GetAPICallResult ok=%v failed=%v", ok, failed)
	}
	return nil
}

func (s *steam) createItem(appID uint32) (createItemResult, error) {
	var res createItemResult
	call := uint64(s.call("SteamAPI_ISteamUGC_CreateItem", s.ugc, uintptr(appID), kEWorkshopFileTypeCommunity))
	err := s.waitCall(call, unsafe.Pointer(&res), unsafe.Sizeof(res), kCreateItemResult, nil)
	if err == nil && res.Result != kEResultOK {
		err = fmt.Errorf("CreateItem EResult %d", res.Result)
	}
	return res, err
}

func (s *steam) addDependency(parent, child uint64) error {
	var res addUGCDependencyResult
	call := uint64(s.call("SteamAPI_ISteamUGC_AddDependency", s.ugc, uintptr(parent), uintptr(child)))
	if err := s.waitCall(call, unsafe.Pointer(&res), unsafe.Sizeof(res), kAddUGCDependencyResult, nil); err != nil {
		return err
	}
	if res.Result != kEResultOK {
		return fmt.Errorf("EResult %d", res.Result)
	}
	return nil
}

// itemUpdate wraps one StartItemUpdate..SubmitItemUpdate cycle.
type itemUpdate struct {
	s *steam
	h uint64
}

func (s *steam) startItemUpdate(appID uint32, fileID uint64) (*itemUpdate, error) {
	h := uint64(s.call("SteamAPI_ISteamUGC_StartItemUpdate", s.ugc, uintptr(appID), uintptr(fileID)))
	if h == invalidUGCUpdateHandle {
		return nil, fmt.Errorf("StartItemUpdate returned invalid handle")
	}
	return &itemUpdate{s, h}, nil
}

func (u *itemUpdate) setStr(fn, v string) error {
	p := cstr(v)
	ok := cbool(u.s.call(fn, u.s.ugc, uintptr(u.h), uintptr(unsafe.Pointer(p))))
	runtime.KeepAlive(p)
	if !ok {
		return fmt.Errorf("%s returned false", fn)
	}
	return nil
}

func (u *itemUpdate) title(v string) error { return u.setStr("SteamAPI_ISteamUGC_SetItemTitle", v) }
func (u *itemUpdate) desc(v string) error {
	return u.setStr("SteamAPI_ISteamUGC_SetItemDescription", v)
}
func (u *itemUpdate) lang(v string) error {
	return u.setStr("SteamAPI_ISteamUGC_SetItemUpdateLanguage", v)
}
func (u *itemUpdate) content(v string) error { return u.setStr("SteamAPI_ISteamUGC_SetItemContent", v) }
func (u *itemUpdate) preview(v string) error { return u.setStr("SteamAPI_ISteamUGC_SetItemPreview", v) }

// addPreviewFile adds an additional image preview (k_EItemPreviewType_Image).
func (u *itemUpdate) addPreviewFile(path string) error {
	p := cstr(path)
	ok := cbool(u.s.call("SteamAPI_ISteamUGC_AddItemPreviewFile", u.s.ugc, uintptr(u.h), uintptr(unsafe.Pointer(p)), kEItemPreviewTypeImage))
	runtime.KeepAlive(p)
	if !ok {
		return fmt.Errorf("AddItemPreviewFile %s returned false", path)
	}
	return nil
}

// previewNames returns the original file names of the item's additional previews.
func (s *steam) previewNames(fileID uint64) ([]string, error) {
	id := fileID
	q := uint64(s.call("SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest", s.ugc, uintptr(unsafe.Pointer(&id)), 1))
	runtime.KeepAlive(&id)
	if q == invalidUGCUpdateHandle { // k_UGCQueryHandleInvalid has the same value
		return nil, fmt.Errorf("CreateQueryUGCDetailsRequest returned invalid handle")
	}
	defer s.call("SteamAPI_ISteamUGC_ReleaseQueryUGCRequest", s.ugc, uintptr(q))
	if !cbool(s.call("SteamAPI_ISteamUGC_SetReturnAdditionalPreviews", s.ugc, uintptr(q), 1)) {
		return nil, fmt.Errorf("SetReturnAdditionalPreviews returned false")
	}
	var res steamUGCQueryCompleted
	call := uint64(s.call("SteamAPI_ISteamUGC_SendQueryUGCRequest", s.ugc, uintptr(q)))
	if err := s.waitCall(call, unsafe.Pointer(&res), unsafe.Sizeof(res), kSteamUGCQueryCompleted, nil); err != nil {
		return nil, err
	}
	if res.Result != kEResultOK || res.NumResults != 1 {
		return nil, fmt.Errorf("query EResult %d, %d results", res.Result, res.NumResults)
	}
	n := uint32(s.call("SteamAPI_ISteamUGC_GetQueryUGCNumAdditionalPreviews", s.ugc, uintptr(q), 0))
	names := make([]string, 0, n)
	for i := range n {
		var url, name [1024]byte
		var typ int32
		if !cbool(s.call("SteamAPI_ISteamUGC_GetQueryUGCAdditionalPreview", s.ugc, uintptr(q), 0, uintptr(i),
			uintptr(unsafe.Pointer(&url[0])), uintptr(len(url)), uintptr(unsafe.Pointer(&name[0])), uintptr(len(name)), uintptr(unsafe.Pointer(&typ)))) {
			return nil, fmt.Errorf("GetQueryUGCAdditionalPreview %d returned false", i)
		}
		names = append(names, gostr(uintptr(unsafe.Pointer(&name[0]))))
	}
	return names, nil
}

func (u *itemUpdate) visibility(v int) error {
	if !cbool(u.s.call("SteamAPI_ISteamUGC_SetItemVisibility", u.s.ugc, uintptr(u.h), uintptr(v))) {
		return fmt.Errorf("SetItemVisibility returned false")
	}
	return nil
}

func (u *itemUpdate) tags(tags []string) error {
	ptrs := make([]*byte, len(tags))
	for i, t := range tags {
		ptrs[i] = cstr(t)
	}
	arr := struct { // SteamParamStringArray_t
		strings **byte
		n       int32
	}{&ptrs[0], int32(len(ptrs))}
	ok := cbool(u.s.call("SteamAPI_ISteamUGC_SetItemTags", u.s.ugc, uintptr(u.h), uintptr(unsafe.Pointer(&arr)), 0))
	runtime.KeepAlive(ptrs)
	runtime.KeepAlive(&arr)
	if !ok {
		return fmt.Errorf("SetItemTags returned false")
	}
	return nil
}

func (u *itemUpdate) gameVersions(min, max string) error {
	pMin, pMax := cstr(min), cstr(max)
	ok := cbool(u.s.call("SteamAPI_ISteamUGC_SetRequiredGameVersions", u.s.ugc, uintptr(u.h), uintptr(unsafe.Pointer(pMin)), uintptr(unsafe.Pointer(pMax))))
	runtime.KeepAlive(pMin)
	runtime.KeepAlive(pMax)
	if !ok {
		return fmt.Errorf("SetRequiredGameVersions returned false")
	}
	return nil
}

func (u *itemUpdate) submit(changeNote string) (submitItemUpdateResult, error) {
	var res submitItemUpdateResult
	var note uintptr
	p := cstr(changeNote)
	if changeNote != "" {
		note = uintptr(unsafe.Pointer(p))
	}
	call := uint64(u.s.call("SteamAPI_ISteamUGC_SubmitItemUpdate", u.s.ugc, uintptr(u.h), note))
	runtime.KeepAlive(p)
	last := time.Time{}
	err := u.s.waitCall(call, unsafe.Pointer(&res), unsafe.Sizeof(res), kSubmitItemUpdateResult, func() {
		if time.Since(last) < 2*time.Second {
			return
		}
		last = time.Now()
		var done, total uint64
		st := u.s.call("SteamAPI_ISteamUGC_GetItemUpdateProgress", u.s.ugc, uintptr(u.h), uintptr(unsafe.Pointer(&done)), uintptr(unsafe.Pointer(&total)))
		if total > 0 {
			fmt.Printf("  status %d: %d/%d bytes\n", st, done, total)
		}
	})
	if err == nil && res.Result != kEResultOK {
		err = fmt.Errorf("SubmitItemUpdate EResult %d", res.Result)
	}
	return res, err
}
