/*
 * loadgen —— 极简 S3 负载生成器（HTTP/1.1 keep-alive）
 *
 * 只做一件事：把一个预签名 URL 打到目标并发，逐请求记录延迟。
 * 不做签名、不解析 XML，因此不会成为签名开销的受害者。
 *
 * 编译：gcc -O2 -pthread -o loadgen loadgen.c
 * 用法：./loadgen -u URL -t 8 -n 2000 [-m PUT -f body.bin] [-w 2]
 *   输出：并发、成功数、错误数、RPS、MB/s、min/mean/p50/p90/p95/p99/max（毫秒）
 */
#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define MAX_SAMPLES 4000000
#define MAX_HEADER 32768

static double *g_lat;
static atomic_long g_n;
static atomic_long g_ok;
static atomic_long g_err;
static atomic_long g_bytes;
static pthread_mutex_t g_lat_mu = PTHREAD_MUTEX_INITIALIZER;

static char *g_path;      /* 请求行里的 path?query */
static char *g_hosthdr;   /* Host 头 */
static char *g_connect_host;
static char *g_port;
static char *g_method = "GET";
static char *g_body;
static long g_body_len;
static int g_warmup_ms;

static double now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec * 1000.0 + ts.tv_nsec / 1e6;
}

static int dial(const char *host, const char *port)
{
    struct addrinfo hints = {0}, *res = NULL;
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    if (getaddrinfo(host, port, &hints, &res) != 0)
        return -1;
    int fd = socket(res->ai_family, res->ai_socktype, res->ai_protocol);
    if (fd < 0) {
        freeaddrinfo(res);
        return -1;
    }
    int one = 1;
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
    struct timeval tv = {.tv_sec = 60, .tv_usec = 0};
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof tv);
    if (connect(fd, res->ai_addr, res->ai_addrlen) != 0) {
        close(fd);
        freeaddrinfo(res);
        return -1;
    }
    freeaddrinfo(res);
    return fd;
}

/* 带缓冲的读：返回已读字节数，0 表示对端关闭，-1 表示错误 */
struct rd {
    char buf[65536];
    size_t pos, len;
    int fd;
};

static int rd_fill(struct rd *r)
{
    ssize_t n = recv(r->fd, r->buf, sizeof r->buf, 0);
    if (n <= 0)
        return n == 0 ? 0 : -1;
    r->pos = 0;
    r->len = (size_t)n;
    return 1;
}

static int rd_byte(struct rd *r)
{
    if (r->pos >= r->len) {
        int rc = rd_fill(r);
        if (rc <= 0)
            return rc;
    }
    return (unsigned char)r->buf[r->pos++];
}

static int rd_exact(struct rd *r, long n, long *counted)
{
    while (n > 0) {
        if (r->pos >= r->len) {
            int rc = rd_fill(r);
            if (rc <= 0)
                return rc;
        }
        size_t avail = r->len - r->pos;
        size_t take = (size_t)n < avail ? (size_t)n : avail;
        r->pos += take;
        n -= (long)take;
        if (counted)
            *counted += (long)take;
    }
    return 1;
}

static int rd_line(struct rd *r, char *out, size_t cap)
{
    size_t i = 0;
    for (;;) {
        int c = rd_byte(r);
        if (c < 0)
            return -1;
        if (c == 0)
            return 0;
        if (c == '\n')
            break;
        if (i + 1 < cap)
            out[i++] = (char)c;
    }
    while (i > 0 && out[i - 1] == '\r')
        i--;
    out[i] = 0;
    return 1;
}

/* 返回 HTTP 状态码；body_bytes 累加负载字节数。expect_body=0 时（HEAD）跳过响应体。 */
static int read_response(struct rd *r, long *body_bytes, int expect_body)
{
    char line[1024];
    if (rd_line(r, line, sizeof line) <= 0)
        return -1;
    int status = 0;
    if (sscanf(line, "HTTP/%*d.%*d %d", &status) != 1)
        return -1;

    long content_length = -1;
    int chunked = 0;
    for (;;) {
        if (rd_line(r, line, sizeof line) <= 0)
            return -1;
        if (line[0] == 0)
            break;
        if (strncasecmp(line, "Content-Length:", 15) == 0)
            content_length = strtol(line + 15, NULL, 10);
        else if (strncasecmp(line, "Transfer-Encoding:", 18) == 0 &&
                 strcasestr(line + 18, "chunked"))
            chunked = 1;
    }

    if (!expect_body) {
        return status;
    }

    if (chunked) {
        for (;;) {
            if (rd_line(r, line, sizeof line) <= 0)
                return -1;
            long sz = strtol(line, NULL, 16);
            if (sz <= 0) {
                rd_line(r, line, sizeof line); /* 结尾空行 */
                break;
            }
            if (rd_exact(r, sz, body_bytes) <= 0)
                return -1;
            rd_line(r, line, sizeof line); /* CRLF */
        }
    } else if (content_length > 0) {
        if (rd_exact(r, content_length, body_bytes) <= 0)
            return -1;
    }
    return status;
}

struct worker {
    int id;
    long requests;
    long ok;
    long err;
    long bytes;
    double *lat;
    long nlat;
};

static void *worker_main(void *arg)
{
    struct worker *w = arg;
    int fd = dial(g_connect_host, g_port);
    struct rd r = {.fd = fd};
    if (fd < 0) {
        w->err = w->requests;
        return NULL;
    }

    size_t hdr_cap = strlen(g_path) + strlen(g_hosthdr) + 256;
    char *hdr = malloc(hdr_cap);
    int hdr_len;
    if (strcmp(g_method, "PUT") == 0 || g_body) {
        hdr_len = snprintf(hdr, hdr_cap,
                           "%s %s HTTP/1.1\r\nHost: %s\r\nContent-Length: %ld\r\n"
                           "Connection: keep-alive\r\n\r\n",
                           g_method, g_path, g_hosthdr, g_body_len);
    } else {
        hdr_len = snprintf(hdr, hdr_cap,
                           "%s %s HTTP/1.1\r\nHost: %s\r\nConnection: keep-alive\r\n\r\n",
                           g_method, g_path, g_hosthdr);
    }
    w->lat = malloc(sizeof(double) * (size_t)w->requests);

    for (long i = 0; i < w->requests; i++) {
        double t0 = now_ms();
        int bad = 0;
        size_t sent = 0;
        while (sent < (size_t)hdr_len) {
            ssize_t n = send(fd, hdr + sent, (size_t)hdr_len - sent, MSG_NOSIGNAL);
            if (n <= 0) { bad = 1; break; }
            sent += (size_t)n;
        }
        if (!bad && g_body_len > 0) {
            long off = 0;
            while (off < g_body_len) {
                ssize_t n = send(fd, g_body + off, (size_t)(g_body_len - off), MSG_NOSIGNAL);
                if (n <= 0) { bad = 1; break; }
                off += n;
            }
        }
        long body_bytes = 0;
        int status = bad ? -1 : read_response(&r, &body_bytes, strcmp(g_method, "HEAD") != 0);
        double dt = now_ms() - t0;

        if (status < 200 || status >= 300) {
            w->err++;
            close(fd);
            fd = dial(g_connect_host, g_port);
            r.fd = fd;
            r.pos = r.len = 0;
            if (fd < 0) {
                w->err += w->requests - i - 1;
                break;
            }
            continue;
        }
        w->ok++;
        w->bytes += body_bytes > 0 ? body_bytes : g_body_len;
        w->lat[w->nlat++] = dt;
    }

    free(hdr);
    if (fd >= 0)
        close(fd);
    return NULL;
}

static int cmp_double(const void *a, const void *b)
{
    double x = *(const double *)a, y = *(const double *)b;
    return x < y ? -1 : (x > y ? 1 : 0);
}

int main(int argc, char **argv)
{
    char *url = NULL;
    int threads = 1;
    long requests = 100;
    char *bodyfile = NULL;

    for (int i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "-u")) url = argv[++i];
        else if (!strcmp(argv[i], "-t")) threads = atoi(argv[++i]);
        else if (!strcmp(argv[i], "-n")) requests = atol(argv[++i]);
        else if (!strcmp(argv[i], "-m")) g_method = argv[++i];
        else if (!strcmp(argv[i], "-f")) bodyfile = argv[++i];
        else if (!strcmp(argv[i], "-w")) g_warmup_ms = atoi(argv[++i]) * 1000;
        else { fprintf(stderr, "未知参数 %s\n", argv[i]); return 2; }
    }
    if (!url || threads < 1 || requests < 1) {
        fprintf(stderr, "用法: loadgen -u URL -t 并发 -n 每线程请求数 [-m PUT -f body] [-w 预热秒]\n");
        return 2;
    }

    /* 解析 http://host:port/path?query */
    char *p = strstr(url, "://");
    if (!p) { fprintf(stderr, "URL 必须以 http:// 开头\n"); return 2; }
    p += 3;
    char *slash = strchr(p, '/');
    if (!slash) { fprintf(stderr, "URL 缺少路径\n"); return 2; }
    g_path = strdup(slash);
    g_hosthdr = strndup(p, (size_t)(slash - p));
    char *hostport = strdup(g_hosthdr);
    char *colon = strrchr(hostport, ':');
    if (colon) { *colon = 0; g_port = colon + 1; }
    else g_port = "80";
    g_connect_host = hostport;

    if (bodyfile) {
        FILE *f = fopen(bodyfile, "rb");
        if (!f) { perror("打开 body 失败"); return 2; }
        fseek(f, 0, SEEK_END);
        g_body_len = ftell(f);
        fseek(f, 0, SEEK_SET);
        g_body = malloc((size_t)g_body_len);
        if (fread(g_body, 1, (size_t)g_body_len, f) != (size_t)g_body_len) {
            fprintf(stderr, "读取 body 失败\n");
            return 2;
        }
        fclose(f);
        if (!getenv("METHOD")) g_method = "PUT";
    }

    /* 预热：先跑一轮小请求，避免把 TCP 慢启动算进首请求 */
    if (g_warmup_ms > 0) {
        struct worker w = {.requests = 20};
        worker_main(&w);
        free(w.lat);
        usleep((useconds_t)g_warmup_ms * 1000);
    }

    g_lat = malloc(sizeof(double) * MAX_SAMPLES);
    pthread_t *tids = calloc((size_t)threads, sizeof(pthread_t));
    struct worker *ws = calloc((size_t)threads, sizeof(struct worker));

    double t0 = now_ms();
    for (int i = 0; i < threads; i++) {
        ws[i].id = i;
        ws[i].requests = requests;
        pthread_create(&tids[i], NULL, worker_main, &ws[i]);
    }
    for (int i = 0; i < threads; i++)
        pthread_join(tids[i], NULL);
    double elapsed = (now_ms() - t0) / 1000.0;

    long ok = 0, err = 0, bytes = 0, nlat = 0;
    for (int i = 0; i < threads; i++) {
        ok += ws[i].ok;
        err += ws[i].err;
        bytes += ws[i].bytes;
        for (long k = 0; k < ws[i].nlat && nlat < MAX_SAMPLES; k++)
            g_lat[nlat++] = ws[i].lat[k];
        free(ws[i].lat);
    }
    qsort(g_lat, (size_t)nlat, sizeof(double), cmp_double);
    double sum = 0;
    for (long i = 0; i < nlat; i++) sum += g_lat[i];
    double mean = nlat ? sum / (double)nlat : 0;
#define P(q) (nlat ? g_lat[(long)((q) * (double)(nlat - 1))] : 0.0)
    printf("c=%-3d ok=%-7ld err=%-4ld rps=%-9.1f MBps=%-8.2f min=%-8.2f mean=%-8.2f "
           "p50=%-8.2f p90=%-8.2f p95=%-8.2f p99=%-8.2f max=%-8.2f\n",
           threads, ok, err, (double)ok / elapsed, (double)bytes / elapsed / 1048576.0,
           nlat ? g_lat[0] : 0.0, mean, P(0.50), P(0.90), P(0.95), P(0.99),
           nlat ? g_lat[nlat - 1] : 0.0);
    return err ? 1 : 0;
}
