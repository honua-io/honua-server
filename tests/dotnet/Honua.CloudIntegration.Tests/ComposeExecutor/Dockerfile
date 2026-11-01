# Stand-in for the product server image. The integration test builds this locally so it can
# prove backup and restore across migration 142 without pulling the multi-gigabyte server image.
FROM python:3.12-alpine
RUN apk add --no-cache postgresql-client
ARG HONUA_FIXTURE_ROLE=rc1
ARG HONUA_MAX_SCHEMA=140
LABEL org.honua.max-schema="${HONUA_MAX_SCHEMA}"
COPY entrypoint.py /entrypoint.py
COPY migrations/ /migrations/
ENV HONUA_FIXTURE_ROLE=${HONUA_FIXTURE_ROLE}
ENV HONUA_MAX_SCHEMA=${HONUA_MAX_SCHEMA}
ENV PYTHONUNBUFFERED=1
EXPOSE 8080
ENTRYPOINT ["python", "-u", "/entrypoint.py"]
