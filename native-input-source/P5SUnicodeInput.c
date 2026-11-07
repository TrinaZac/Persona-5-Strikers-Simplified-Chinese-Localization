#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <MinHook.h>
#include "input-coverage.h"

static HMODULE plugin_module;
static volatile LONG started;
static volatile LONG status_code;
typedef BYTE *(*CharacterParser)(BYTE *, const BYTE *, BYTE *);
static CharacterParser original_parser;

static BOOL supported_hanzi(const BYTE *input) {
    unsigned int a = input[0], b, c, codepoint;
    if (a < 0xE0 || a > 0xEF) return FALSE;
    b = input[1];
    if ((b & 0xC0) != 0x80) return FALSE;
    c = input[2];
    if ((c & 0xC0) != 0x80) return FALSE;
    codepoint = ((a & 15) << 12) | ((b & 63) << 6) | (c & 63);
    return codepoint >= 0x800
           && (input_coverage[codepoint >> 3] & (1 << (codepoint & 7))) != 0;
}

static BYTE *parse_with_patch_fonts(BYTE *context, const BYTE *input, BYTE *output) {
    BYTE *end = original_parser(context, input, output);
    /* The game's legacy index remains available for its metrics. The G1N
       renderer resolves the retained UTF-8 text through the patched cmaps. */
    if (!output[16] && *(DWORD *)(context + 0x2F320) == 7 && supported_hanzi(input))
        output[16] = 1;
    return end;
}

typedef struct InputPatch {
    DWORD offset;
    BYTE original[5];
    BYTE patched[5];
} InputPatch;

static const InputPatch patches[2] = {
    /* IME result: use UTF-8 instead of the language-specific legacy code page. */
    { 0xC92D4, {0xE8,0x6B,0x2C,0xFB,0xFF}, {0xB8,0xE9,0xFD,0x00,0x00} },
    /* The buffer is already UTF-8: advance to the game's UTF-8 commit copy. */
    { 0xC9321, {0xE8,0x04,0x83,0xF5,0xFF}, {0xE9,0xEC,0x00,0x00,0x00} }
};

static BOOL equal_bytes(const BYTE *left, const BYTE *right, DWORD size) {
    DWORD i;
    for (i = 0; i < size; ++i) {
        if (left[i] != right[i]) return FALSE;
    }
    return TRUE;
}

static void write_log(const char *message) {
    WCHAR path[MAX_PATH];
    DWORD length, i, written, bytes = 0;
    HANDLE file;
    static const WCHAR suffix[] = L"P5SUnicodeInput.log";
    length = GetModuleFileNameW(plugin_module, path, MAX_PATH);
    if (!length || length >= MAX_PATH) return;
    i = length;
    while (i && path[i - 1] != L'\\' && path[i - 1] != L'/') --i;
    if (i + sizeof(suffix) / sizeof(WCHAR) > MAX_PATH) return;
    length = 0;
    do { path[i + length] = suffix[length]; } while (suffix[length++]);
    file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                       NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return;
    while (message[bytes]) ++bytes;
    WriteFile(file, message, bytes, &written, NULL);
    CloseHandle(file);
}

static BOOL is_game(BYTE *base) {
    WCHAR path[MAX_PATH];
    DWORD length, start;
    IMAGE_DOS_HEADER *dos;
    IMAGE_NT_HEADERS64 *nt;
    length = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (!length || length >= MAX_PATH) return FALSE;
    start = length;
    while (start && path[start - 1] != L'\\' && path[start - 1] != L'/') --start;
    if (lstrcmpiW(path + start, L"game.exe") != 0) return FALSE;
    dos = (IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew < 64 || dos->e_lfanew > 0x100000) return FALSE;
    nt = (IMAGE_NT_HEADERS64 *)(base + dos->e_lfanew);
    return nt->Signature == IMAGE_NT_SIGNATURE && nt->FileHeader.Machine == IMAGE_FILE_MACHINE_AMD64
           && nt->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC
           && nt->OptionalHeader.SizeOfImage >= 0x24A8508;
}

static DWORD WINAPI apply_input_patch(LPVOID unused) {
    BYTE *base = (BYTE *)GetModuleHandleW(NULL);
    DWORD i, j, old_protection, restored_protection;
    BYTE previous[2][5];
    BYTE *page;
    MH_STATUS hook_status;
    static const BYTE parser_prologue[14] = {
        0x48,0x89,0x5C,0x24,0x18,0x48,0x89,0x6C,0x24,0x20,0x56,0x57,0x41,0x57
    };
    (void)unused;
    if (!is_game(base)) {
        status_code = 3;
        write_log("Unsupported host executable; input patch was not applied.\r\n");
        return 0;
    }
    /* Initialization happens outside DllMain. Allow the game's startup to settle. */
    Sleep(1500);
    for (i = 0; i < 2; ++i) {
        BYTE *target = base + patches[i].offset;
        if (!equal_bytes(target, patches[i].original, 5) && !equal_bytes(target, patches[i].patched, 5)) {
            status_code = 3;
            write_log("Input code differs from supported build; input patch was not applied.\r\n");
            return 0;
        }
        for (j = 0; j < 5; ++j) previous[i][j] = target[j];
    }
    if (!equal_bytes(base + 0x8406B0, parser_prologue, sizeof(parser_prologue))) {
        status_code = 3;
        write_log("Character parser differs from supported build; input patch was not applied.\r\n");
        return 0;
    }
    hook_status = MH_Initialize();
    if (hook_status != MH_OK) {
        status_code = 4;
        write_log("Character parser hook initialization failed.\r\n");
        return 0;
    }
    hook_status = MH_CreateHook(base + 0x8406B0, parse_with_patch_fonts, (LPVOID *)&original_parser);
    if (hook_status != MH_OK) {
        MH_Uninitialize();
        status_code = 4;
        write_log("Character parser trampoline creation failed.\r\n");
        return 0;
    }
    page = base + 0xC9000;
    if (!VirtualProtect(page, 0x1000, PAGE_EXECUTE_READWRITE, &old_protection)) {
        MH_Uninitialize();
        status_code = 4;
        write_log("VirtualProtect failed.\r\n");
        return 0;
    }
    hook_status = MH_EnableHook(base + 0x8406B0);
    if (hook_status != MH_OK) {
        VirtualProtect(page, 0x1000, old_protection, &restored_protection);
        MH_Uninitialize();
        status_code = 4;
        write_log("Character parser hook activation failed.\r\n");
        return 0;
    }
    /* Both sites share one code page. Install the bypass before UTF-8 selection. */
    for (i = 2; i > 0; --i) {
        const InputPatch *patch = &patches[i - 1];
        BYTE *target = base + patch->offset;
        for (j = 0; j < 5; ++j) target[j] = patch->patched[j];
    }
    FlushInstructionCache(GetCurrentProcess(), page, 0x1000);
    if (!VirtualProtect(page, 0x1000, old_protection, &restored_protection)) {
        for (i = 0; i < 2; ++i)
            for (j = 0; j < 5; ++j) base[patches[i].offset + j] = previous[i][j];
        FlushInstructionCache(GetCurrentProcess(), page, 0x1000);
        VirtualProtect(page, 0x1000, old_protection, &restored_protection);
        MH_Uninitialize();
        status_code = 4;
        write_log("Restoring page protection failed; input changes rolled back.\r\n");
        return 0;
    }
    for (i = 0; i < 2; ++i) {
        if (!equal_bytes(base + patches[i].offset, patches[i].patched, 5)) {
            status_code = 4;
            write_log("Input patch verification failed.\r\n");
            return 0;
        }
    }
    status_code = 2;
    write_log("P5S Unicode Input: UTF-8 IME commit and font-aware Hanzi validation enabled (27505 codepoints).\r\n");
    return 0;
}

__declspec(dllexport) void InitializeASI(void) {
    HANDLE worker;
    if (InterlockedCompareExchange(&started, 1, 0) != 0) return;
    status_code = 1;
    worker = CreateThread(NULL, 0, apply_input_patch, NULL, 0, NULL);
    if (worker) CloseHandle(worker);
    else {
        status_code = 4;
        write_log("Failed to start input initialization worker.\r\n");
    }
}

__declspec(dllexport) LONG P5SUnicodeInputGetStatus(void) {
    return status_code;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        plugin_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
