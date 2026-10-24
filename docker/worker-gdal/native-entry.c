/* Copyright (c) Honua Geo Inc. Licensed under the Fair Core License, Version 1.0, ALv2 Future License.
 * Qualification-only pause AFTER actual ELF exec, before main. Never an
 * assertion that GDAL has processed data or reached a mid-operation phase. */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

static void refuse(void) { _exit(125); }

/* Walk every directory component without following a link, including ancestors. */
static int parent_directory(const char *path, char *name) {
    if (!path || path[0] != '/' || strlen(path) >= PATH_MAX) refuse();
    char copy[PATH_MAX];
    strcpy(copy, path + 1);
    int fd = open("/", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    if (fd < 0) refuse();
    char *part = copy, *slash;
    while ((slash = strchr(part, '/')) != NULL) {
        *slash = 0;
        if (!*part || !strcmp(part, ".") || !strcmp(part, "..")) refuse();
        int next = openat(fd, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        close(fd);
        if (next < 0) refuse();
        fd = next; part = slash + 1;
    }
    if (!*part || !strcmp(part, ".") || !strcmp(part, "..")) refuse();
    strcpy(name, part);
    return fd;
}

__attribute__((constructor)) static void native_entry(void) {
    const char *record = getenv("HONUA_GP_NATIVE_ENTRY_RECORD");
    const char *release = getenv("HONUA_GP_NATIVE_ENTRY_RELEASE");
    const char *nonce = getenv("HONUA_GP_NATIVE_ENTRY_NONCE");
    if (!record && !release && !nonce) return;
    if (!record || !release || !nonce || strlen(nonce) != 32 ||
        strspn(nonce, "0123456789abcdef") != 32) refuse();
    char record_name[PATH_MAX], release_name[PATH_MAX], executable[PATH_MAX];
    int directory = parent_directory(record, record_name);
    int release_directory = parent_directory(release, release_name);
    struct stat first, second;
    if (fstat(directory, &first) || fstat(release_directory, &second) ||
        first.st_dev != second.st_dev || first.st_ino != second.st_ino) refuse();
    close(release_directory);
    ssize_t length = readlink("/proc/self/exe", executable, sizeof(executable) - 1);
    if (length <= 0 || length >= (ssize_t)sizeof(executable) - 1) refuse();
    executable[length] = 0;
    if (strpbrk(executable, "\"\\\n\r\t")) refuse();
    char temporary[64], json[PATH_MAX + 256];
    snprintf(temporary, sizeof(temporary), "native-entry-%s.tmp", nonce);
    int fd = openat(directory, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0600);
    if (fd < 0) refuse();
    pid_t parent = getppid();
    int bytes = snprintf(json, sizeof(json),
        "{\"schema\":\"honua.gp-native-entry/v1\",\"boundary\":\"native-exec-entry-paused\","
        "\"pid\":%ld,\"parentPid\":%ld,\"nonce\":\"%s\",\"executable\":\"%s\"}",
        (long)getpid(), (long)parent, nonce, executable);
    if (bytes <= 0 || bytes >= (int)sizeof(json) || write(fd, json, (size_t)bytes) != bytes || fsync(fd)) refuse();
    close(fd);
    /* Atomic publication refuses an existing record (including a symlink). */
    if (linkat(directory, temporary, directory, record_name, 0)) refuse();
    unlinkat(directory, temporary, 0);
    struct timespec start, now, pause = {0, 25000000};
    if (clock_gettime(CLOCK_MONOTONIC, &start)) refuse();
    for (;;) {
        if (getppid() != parent) refuse();
        fd = openat(directory, release_name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC);
        if (fd >= 0) {
            char value[33];
            struct stat info;
            if (fstat(fd, &info) || !S_ISREG(info.st_mode) || info.st_size != 32 || read(fd, value, sizeof(value)) != 32 ||
                memcmp(value, nonce, 32)) refuse();
            close(fd); close(directory); return;
        }
        if (errno != ENOENT || getppid() != parent || clock_gettime(CLOCK_MONOTONIC, &now) ||
            now.tv_sec - start.tv_sec >= 300) refuse();
        nanosleep(&pause, NULL);
    }
}
