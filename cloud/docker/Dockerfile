# Robot Vacuum Simulator - headless cloud node (requirements 2.17, 3.9, 5.5)
#
# Packages the Unity Linux Dedicated Server build of the simulator with a small Python entrypoint that
# fetches the job file from S3, runs this node's shard, and streams results back to S3.
#
# Build context = this folder, with the Unity Linux server build copied into ./build/ first:
#   cloud/docker/build/RobotVacuumServer.x86_64
#   cloud/docker/build/RobotVacuumServer_Data/...
# CI does this (see .github/workflows/cloud.yml); locally: `python -m vacuumctl push-image --build-dir <dir>`.

FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    VACUUM_HOME=/opt/vacuum

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates python3 python3-pip \
 && pip3 install --no-cache-dir "boto3>=1.34,<2" \
 && apt-get purge -y python3-pip && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*

# Never run the simulator as root (C.12, 4.8).
RUN useradd --create-home --uid 1000 vacuum && mkdir -p /work && chown vacuum:vacuum /work

COPY --chown=vacuum:vacuum build/ /opt/vacuum/
COPY entrypoint.py /opt/vacuum-entrypoint/entrypoint.py
RUN find /opt/vacuum -maxdepth 1 -name "*.x86_64" -exec chmod +x {} \;

ARG GIT_SHA=unknown
LABEL org.opencontainers.image.title="robot-vacuum-sim-headless" \
      org.opencontainers.image.revision="${GIT_SHA}"
ENV VACUUM_IMAGE_REVISION=${GIT_SHA}

USER vacuum
WORKDIR /work
ENTRYPOINT ["python3", "/opt/vacuum-entrypoint/entrypoint.py"]
