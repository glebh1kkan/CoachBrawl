// coach_redirect.c — автo-редирект CoachBrawl на свой сервер, без ПК и frida.
// хукает connect() во всех загруженных .so: TCP на порт 9339 ведёт на 150.241.70.48.
// сборка: см. apk_patch/README.md (нужен android-ndk).
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <pthread.h>
#include <dlfcn.h>
#include <link.h>
#include <elf.h>
#include <sys/mman.h>
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <android/log.h>

#define LOG_TAG "coach"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)

#define GAME_TCP_PORT 9339
static const unsigned char SERVER_IP[4] = {150, 241, 70, 48};

typedef int (*connect_func_t)(int, const struct sockaddr *, socklen_t);
static connect_func_t real_connect = NULL;

static int hooked_connect(int sockfd, const struct sockaddr *addr, socklen_t addrlen) {
    if (addr != NULL && addrlen >= sizeof(struct sockaddr_in) && addr->sa_family == AF_INET) {
        const struct sockaddr_in *in4 = (const struct sockaddr_in *)addr;
        if (ntohs(in4->sin_port) == GAME_TCP_PORT) {
            struct sockaddr_in dst = *in4;
            memcpy(&dst.sin_addr.s_addr, SERVER_IP, 4);
            LOGI("tcp %s:%d -> 150.241.70.48:%d", inet_ntoa(in4->sin_addr),
                 (int)ntohs(in4->sin_port), GAME_TCP_PORT);
            return real_connect(sockfd, (const struct sockaddr *)&dst, addrlen);
        }
    }
    return real_connect(sockfd, addr, addrlen);
}

#if defined(__aarch64__)
#define REL_TYPE_JUMP R_AARCH64_JUMP_SLOT
typedef Elf64_Rela hook_rel_t;
typedef Elf64_Sym hook_sym_t;
#define ELF_R_SYM ELF64_R_SYM
#define ELF_R_TYPE ELF64_R_TYPE
#else
#define REL_TYPE_JUMP R_ARM_JUMP_SLOT
typedef Elf32_Rel hook_rel_t;
typedef Elf32_Sym hook_sym_t;
#define ELF_R_SYM ELF32_R_SYM
#define ELF_R_TYPE ELF32_R_TYPE
#endif

static int hook_connect_plt(uintptr_t base, const char *name) {
    ElfW(Phdr) *phdr = NULL;
    ElfW(Half) phnum = 0;
    // находим PT_DYNAMIC через dl_iterate_phdrs-данные: base = dlpi_addr
    // читаем ehdr напрямую
    ElfW(Ehdr) *ehdr = (ElfW(Ehdr) *)base;
    if (memcmp(ehdr->e_ident, ELFMAG, SELFMAG) != 0) return 0;
    phdr = (ElfW(Phdr) *)(base + ehdr->e_phoff);
    phnum = ehdr->e_phnum;

    ElfW(Dyn) *dyn = NULL;
    for (int i = 0; i < phnum; i++) {
        if (phdr[i].p_type == PT_DYNAMIC) { dyn = (ElfW(Dyn) *)(base + phdr[i].p_vaddr); break; }
    }
    if (!dyn) return 0;

    hook_rel_t *jmprel = NULL;
    hook_sym_t *symtab = NULL;
    const char *strtab = NULL;
    size_t pltrelsz = 0;
    for (ElfW(Dyn) *d = dyn; d->d_tag != DT_NULL; d++) {
        if (d->d_tag == DT_JMPREL) jmprel = (hook_rel_t *)(base + d->d_un.d_ptr);
        else if (d->d_tag == DT_SYMTAB) symtab = (hook_sym_t *)(base + d->d_un.d_ptr);
        else if (d->d_tag == DT_STRTAB) strtab = (const char *)(base + d->d_un.d_ptr);
        else if (d->d_tag == DT_PLTRELSZ) pltrelsz = d->d_un.d_val;
    }
    if (!jmprel || !symtab || !strtab) return 0;

    size_t count = pltrelsz / sizeof(hook_rel_t);
    int hooked = 0;
    for (size_t i = 0; i < count; i++) {
#if defined(__aarch64__)
        unsigned sym = ELF_R_SYM(jmprel[i].r_info);
        unsigned type = ELF_R_TYPE(jmprel[i].r_info);
        uintptr_t *where = (uintptr_t *)(base + jmprel[i].r_offset);
#else
        unsigned sym = ELF_R_SYM(jmprel[i].r_info);
        unsigned type = ELF_R_TYPE(jmprel[i].r_info);
        uintptr_t *where = (uintptr_t *)(base + jmprel[i].r_offset);
#endif
        if (type != REL_TYPE_JUMP) continue;
        const char *sname = strtab + symtab[sym].st_name;
        if (strcmp(sname, "connect") != 0) continue;
        if ((void *)*where == (void *)hooked_connect) { hooked = 1; continue; }
        if (real_connect == NULL) real_connect = (connect_func_t)*where;
        long pagesz = sysconf(_SC_PAGESIZE);
        uintptr_t page = (uintptr_t)where & ~(pagesz - 1);
        if (mprotect((void *)page, pagesz, PROT_READ | PROT_WRITE) != 0) continue;
        *where = (uintptr_t)hooked_connect;
        mprotect((void *)page, pagesz, PROT_READ | PROT_EXEC);
        LOGI("hooked connect in %s", name);
        hooked = 1;
    }
    return hooked;
}

static int scan_cb(struct dl_phdr_info *info, size_t size, void *data) {
    (void)size; (void)data;
    if (info->dlpi_addr == 0) return 0;
    const char *name = info->dlpi_name;
    if (name == NULL || name[0] == '\0') return 0;
    if (strstr(name, ".so") == NULL) return 0;
    hook_connect_plt(info->dlpi_addr, name);
    return 0;
}

static void scan_all(void) {
    if (real_connect == NULL) {
        real_connect = (connect_func_t)dlsym(RTLD_NEXT, "connect");
    }
    dl_iterate_phdr(scan_cb, NULL);
}

static void *watcher_thread(void *arg) {
    (void)arg;
    for (int i = 0; i < 120; i++) { // 60 сек догоняем поздно подгруженные либы
        scan_all();
        usleep(500 * 1000);
    }
    return NULL;
}

__attribute__((constructor)) static void coach_init(void) {
    LOGI("coach_redirect loaded, game tcp 9339 -> 150.241.70.48");
    scan_all();
    pthread_t th;
    pthread_create(&th, NULL, watcher_thread, NULL);
    pthread_detach(th);
}
