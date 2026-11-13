#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

extern char **environ;

static char *read_secret(const char *path)
{
    FILE *file = fopen(path, "rb");
    if (file == NULL)
        return NULL;
    size_t capacity = 256, length = 0;
    char *value = malloc(capacity);
    int byte;
    while (value != NULL && (byte = fgetc(file)) != EOF) {
        if (byte == '\0') {
            free(value);
            value = NULL;
            break;
        }
        if (length + 1 == capacity) {
            capacity *= 2;
            char *grown = realloc(value, capacity);
            if (grown == NULL) {
                free(value);
                value = NULL;
                break;
            }
            value = grown;
        }
        value[length++] = (char)byte;
    }
    if (ferror(file) || length == 0) {
        free(value);
        value = NULL;
    }
    fclose(file);
    if (value != NULL) {
        while (length > 0 && value[length - 1] == '\n')
            --length;
        value[length] = '\0';
    }
    return value;
}

static int load_setting(char *reference)
{
    const char prefix[] = "HONUA_SECRET_FILE__";
    char *separator = strchr(reference, '=');
    if (separator == NULL || strncmp(reference, prefix, sizeof(prefix) - 1) != 0)
        return 0;
    *separator = '\0';
    char *value = read_secret(separator + 1);
    if (value == NULL || value[0] == '\0') {
        free(value);
        return 1;
    }
    int result = setenv(reference + sizeof(prefix) - 1, value, 1);
    free(value);
    return result == 0 ? unsetenv(reference) != 0 : 1;
}

static int load_settings(void)
{
    size_t count = 0;
    while (environ[count] != NULL)
        ++count;
    char **snapshot = calloc(count + 1, sizeof(char *));
    if (snapshot == NULL)
        return 1;
    int result = 0;
    for (size_t index = 0; index < count; ++index) {
        snapshot[index] = strdup(environ[index]);
        if (snapshot[index] == NULL) {
            result = 1;
            break;
        }
    }
    for (size_t index = 0; index < count && result == 0; ++index)
        result = load_setting(snapshot[index]);
    for (size_t index = 0; index < count; ++index)
        free(snapshot[index]);
    free(snapshot);
    return result;
}

static int load_database_password(void)
{
    const char *path = getenv("HONUA_DATABASE_PASSWORD_FILE");
    if (path == NULL || path[0] == '\0')
        return 0;
    char *password = read_secret(path);
    if (password == NULL || password[0] == '\0') {
        free(password);
        return 1;
    }
    const char *connection = getenv("ConnectionStrings__DefaultConnection");
    if (connection == NULL)
        connection = "";
    size_t capacity = strlen(connection) + strlen(password) + sizeof(";Password=");
    char *value = malloc(capacity);
    if (value == NULL) {
        free(password);
        return 1;
    }
    snprintf(value, capacity, "%s;Password=%s", connection, password);
    int result = setenv("ConnectionStrings__DefaultConnection", value, 1);
    free(value);
    free(password);
    return result == 0 ? unsetenv("HONUA_DATABASE_PASSWORD_FILE") != 0 : 1;
}

int main(int argc, char **argv)
{
    if (argc < 2 || load_settings() != 0 || load_database_password() != 0) {
        fputs("Unable to load secret files; refusing startup.\n", stderr);
        return 1;
    }
    execvp(argv[1], argv + 1);
    fputs("Unable to start application.\n", stderr);
    return 1;
}
