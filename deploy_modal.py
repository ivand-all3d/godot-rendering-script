"""
Simple script to dispatch a modal job to render All3D assets.

Uses `blender_script.py` to render assets, loads models from AWS
and saves them to AWS.
"""

import modal
import boto3
from tqdm import tqdm

from pathlib import Path
import os
import time
from functools import partial
from concurrent.futures import ThreadPoolExecutor, as_completed

VIEW_COUNT = 150
COUNT_LIMIT = -10000
OFFSET = 0
EXCLUDE_IDS = [45145]  # Some GLBs take very long to render
INCLUDE_IDS = None  # If set, takes precedence of COUNT_LIMIT/OFFSET

RENDER_TIMEOUT = 2000  # Seems to take about 10 minutes to render, leaving some wiggle room for uploading
CONCURRENCY_LIMIT = 20
STOP_ON_ERROR = False

SOURCE_BUCKET = "all3d"
SOURCE_PREFIX = "product_assets/glb/"

TARGET_BUCKET = "all3d-dev"
TARGET_PREFIX = "test-renders-1000/"
GPU = "T4"

container_image = (
    modal.Image.debian_slim(python_version="3.11")
    .apt_install("xorg", "libxkbcommon0", "curl", "unzip")
    .shell(["/bin/bash", "--login", "-c"])
    .run_commands(
        [
            "curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.1/install.sh | bash",
            "nvm install 22",
            "npm install -g @gltf-transform/cli",
        ]
    )
    .pip_install_from_pyproject("pyproject.toml")
    .add_local_python_source("PolyhavenHDRI")
    .add_local_python_source("Conversions")
    .add_local_python_source("BlenderUtils")
    .add_local_python_source("blender_script")
    .add_local_dir(Path(__file__).parent / ".polyhaven", "/root/.polyhaven")
)
app = modal.App("render_all3d_dataset")


def download_object_s3(model_id: str, bucket: str, prefix: str) -> str:
    def download_object(s3_client, bucket_name, prefix, obj_key, output_dir):
        local_path = os.path.join(output_dir, obj_key)
        s3_client.download_file(
            bucket_name, os.path.join(prefix, obj_key), local_path
        )
        return local_path

    s3_client = boto3.client("s3")

    download_object(s3_client, bucket, prefix, f"{model_id}.glb", "/tmp")

    return f"/tmp/{model_id}.glb"


def upload_file_s3(path: str, bucket: str, prefix: str):
    s3_client = boto3.client("s3")
    s3_path = f"{prefix}{os.path.basename(path)}"
    s3_client.upload_file(path, bucket, s3_path)


def upload_folder_s3(local_folder: str, bucket: str, prefix: str):
    s3_client = boto3.client("s3")
    for root, _, files in os.walk(local_folder):
        for file in files:
            local_path = os.path.join(root, file)
            relative_path = os.path.relpath(local_path, local_folder).lstrip("./")
            s3_path = f"{prefix}{relative_path}"

            s3_client.upload_file(local_path, bucket, s3_path)


@app.function(
    image=container_image,
    gpu=GPU,
    secrets=[modal.Secret.from_name("aws-secret")],
    timeout=RENDER_TIMEOUT,
    max_containers=CONCURRENCY_LIMIT,
)
def remote_main(id):
    print("Importing blender...")
    from blender_script import Renderer, CameraFormation, RenderEngine, Illuminant

    print(f"Downloading object {id}...")
    time_download = time.time()
    file_path = download_object_s3(id, SOURCE_BUCKET, SOURCE_PREFIX)
    time_download = time.time() - time_download
    print(f"Downloaded at [{file_path}]")

    print(f"Removing DRACO compression...")
    time_decompress = time.time()
    os.system(
        f"bash -ic 'gltf-transform optimize {file_path} {file_path} --compress false'"
    )
    time_decompress = time.time() - time_decompress

    print("Rendering...")
    pool = ThreadPoolExecutor(max_workers=10)

    def on_render_frame(source_path, idx, dir, files):
        fn = partial(
            upload_file_s3,
            bucket=TARGET_BUCKET,
            prefix=f"{TARGET_PREFIX}{Path(source_path).stem}/",
        )
        pool.map(fn, files)

    time_render = time.time()
    renderer = Renderer()
    renderer.run(file_path, 1024, VIEW_COUNT, "output", on_render_frame=on_render_frame)
    time_render = time.time() - time_render

    # print("Uploading results to AWS...")
    # time_upload = time.time()
    # upload_folder_s3("output", TARGET_BUCKET, f"{TARGET_PREFIX}{id}/")
    # time_upload = time.time() - time_upload
    print("Wrapping up uploading to AWS...")
    time_upload = time.time()
    upload_file_s3("output/metadata.json", TARGET_BUCKET, f"{TARGET_PREFIX}{id}/")
    pool.shutdown(wait=True)
    time_upload = time.time() - time_upload

    time_total = time_render + time_upload + time_download + time_decompress
    print("Done, time elapsed:")
    print(
        f" - Download: {time_download:.2f} ({(time_download / time_total) * 100:.1f}%)"
    )
    print(f" - Upload: {time_upload:.2f} ({(time_upload / time_total) * 100:.1f}%)")
    print(
        f" - Decompress: {time_decompress:.2f} ({(time_decompress / time_total) * 100:.1f}%)"
    )
    print(f" - Render: {time_render:.2f} ({(time_render / time_total) * 100:.1f}%)")
    print(f" - Total: {time_total:.2f}")
    return None


@app.local_entrypoint()
def main():
    global COUNT_LIMIT
    print("Starting deploy render:")
    print(f" - Downloading from: s3://{SOURCE_BUCKET}/{SOURCE_PREFIX}")
    print(f" - Uploading to: s3://{TARGET_BUCKET}/{TARGET_PREFIX}")
    print(f" - Count limit: {COUNT_LIMIT}")

    # Get list of all glbs
    print("Listing target GLBs...")
    s3_client = boto3.client("s3")
    paginator = s3_client.get_paginator("list_objects_v2")
    pages = paginator.paginate(Bucket=SOURCE_BUCKET, Prefix=SOURCE_PREFIX)

    glb_files = []
    for page in tqdm(pages):
        files = [
            os.path.splitext(obj["Key"].split("/")[-1])[0]
            for obj in page["Contents"]
            if obj["Key"].endswith(".glb") and os.path.dirname(obj["Key"]) == os.path.dirname(SOURCE_PREFIX)
        ]
        glb_files.extend(files)
    glb_files = list(set([int(x) for x in glb_files if x.isdigit()]))  # Unique
    glb_files = sorted(glb_files)  # Sorted
    if (
        COUNT_LIMIT != None and COUNT_LIMIT < 0
    ):  # Negative count_limit grabs from the end
        glb_files = list(reversed(glb_files))  # Descending
        COUNT_LIMIT = -COUNT_LIMIT
    glb_files = list(
        filter(lambda f: f not in EXCLUDE_IDS, glb_files)
    )  # Filter EXCLUDE_IDS
    if INCLUDE_IDS != None:
        glb_files = list(filter(lambda f: f in INCLUDE_IDS, glb_files))
    elif COUNT_LIMIT != None:
        glb_files = glb_files[OFFSET : (OFFSET + COUNT_LIMIT)]

    # Filter existing glbs
    original_len = len(glb_files)
    print("Filtering GLBs...")

    def check_file(f):
        return s3_client.list_objects_v2(
            Bucket=TARGET_BUCKET,
            Prefix=f"{TARGET_PREFIX}{f}/",
            MaxKeys=VIEW_COUNT * 5 + 2
        ).get("KeyCount", 0) < VIEW_COUNT * 5 + 1

    filtered_glbs = []
    with ThreadPoolExecutor() as pool:
        future_to_file = {
            pool.submit(check_file, f): f for f in glb_files
        }
        for future in tqdm(as_completed(future_to_file), total=len(future_to_file)):
            if future.result():
                filtered_glbs.append(future_to_file[future])
    glb_files = filtered_glbs

    # Call remote function to start render
    print(f"Processing IDs: {glb_files}")
    print(f" - Rendering {len(glb_files)}/{original_len} GLB files...")
    results = list(remote_main.map(glb_files, return_exceptions=not STOP_ON_ERROR))
    error_ids = [id for id, err in zip(glb_files, results) if err is not None]
    print(f"Got errors for IDs: {error_ids}")
    print(f" - {list(filter(lambda x: x is not None, results))}")
