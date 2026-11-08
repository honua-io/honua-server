#include <arpa/inet.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    if (argc > 2 || (argc == 2 && strcmp(argv[1], "--ready") != 0))
        return 1;
    const char *path = argc == 2 ? "/healthz/ready" : "/healthz/live";
    const char *configured_port = getenv("HONUA_HEALTH_PORT");
    char *end;
    long port = 8080;
    if (configured_port != NULL) {
        errno = 0;
        port = strtol(configured_port, &end, 10);
        if (errno != 0 || end == configured_port || *end != '\0' || port < 1 || port > 65535)
            return 1;
    }

    int connection = socket(AF_INET, SOCK_STREAM, 0);
    if (connection < 0)
        return 1;

    struct timeval timeout = { .tv_sec = 3, .tv_usec = 0 };
    if (setsockopt(connection, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) != 0 ||
        setsockopt(connection, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout)) != 0) {
        close(connection);
        return 1;
    }
    struct sockaddr_in address = {
        .sin_family = AF_INET,
        .sin_port = htons((unsigned short)port),
        .sin_addr.s_addr = htonl(INADDR_LOOPBACK)
    };
    if (connect(connection, (struct sockaddr *)&address, sizeof(address)) != 0) {
        close(connection);
        return 1;
    }

    char request[128];
    int request_length = snprintf(request, sizeof(request),
        "GET %s HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n", path);
    if (request_length < 0 || (size_t)request_length >= sizeof(request)) {
        close(connection);
        return 1;
    }
    size_t sent = 0;
    while (sent < (size_t)request_length) {
        ssize_t count = send(connection, request + sent, (size_t)request_length - sent, MSG_NOSIGNAL);
        if (count <= 0) {
            close(connection);
            return 1;
        }
        sent += (size_t)count;
    }

    char response[128];
    size_t received = 0;
    while (received < sizeof(response) - 1) {
        ssize_t count = recv(connection, response + received, sizeof(response) - 1 - received, 0);
        if (count <= 0)
            break;
        received += (size_t)count;
        response[received] = '\0';
        if (strchr(response, '\n') != NULL)
            break;
    }
    close(connection);
    response[received] = '\0';
    unsigned int major = 0, minor = 0, status = 0;
    char separator = '\0';
    if (strchr(response, '\n') == NULL ||
        sscanf(response, "HTTP/%u.%u %u%c", &major, &minor, &status, &separator) != 4 ||
        major != 1 || (separator != ' ' && separator != '\r'))
        return 1;
    return status == 200 ? 0 : 1;
}
