/*
 * Runs a program inside the chiseled runtime root with the dynamic loader's trace
 * environment. The root has no shell or env, and the build image's env may depend on
 * libraries the root lacks, so verify-native.sh links this helper statically.
 * Usage: verify-env <program> [args...]
 */
#include <stdio.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    char *environment[] = {"LD_TRACE_LOADED_OBJECTS=1", "LD_WARN=yes", "LD_BIND_NOW=yes", NULL};

    if (argc < 2) {
        fputs("usage: verify-env <program> [args...]\n", stderr);
        return 2;
    }
    execve(argv[1], argv + 1, environment);
    perror("verify-env: execve");
    return 127;
}
