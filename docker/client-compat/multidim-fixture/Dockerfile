FROM python:3.12-slim

RUN pip install --no-cache-dir boto3==1.40.21 netCDF4==1.7.2 numpy==2.3.2

WORKDIR /fixture
COPY docker/client-compat/multidim-fixture/seed.py /fixture/seed.py
COPY docker/client-compat/multidim-fixture/verify.py /fixture/verify.py

