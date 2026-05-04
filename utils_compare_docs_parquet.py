# Databricks notebook source
# MAGIC %pip install --upgrade python-docx
# MAGIC

# COMMAND ----------

from pyspark.sql import SparkSession
from pyspark.ml.feature import HashingTF, IDF, Tokenizer, StringIndexer, IndexToString
from pyspark.ml.classification import LogisticRegression
from pyspark.ml import Pipeline
from pyspark.sql import SparkSession
from pyspark.sql.functions import concat_ws, col
from pyspark.sql.functions import regexp_extract

from pyspark.sql import functions as F, types as T
import io, json, csv, email
import base64, json, os

# Initialize SparkSession for Databricks
# In Databricks, the SparkSession is typically pre-configured,
# so you don't need to explicitly set Hadoop Azure dependencies.
spark = (
    SparkSession.builder
    .appName("Document-Classification")
    .getOrCreate()
)

configs = {
  "fs.azure.account.auth.type": "OAuth",
  "fs.azure.account.oauth.provider.type": "org.apache.hadoop.fs.azurebfs.oauth2.ClientCredsTokenProvider",
  "fs.azure.account.oauth2.client.id": "640059f9-cebf-47e5-a3f1-7bbe06e7e4a3",
  "fs.azure.account.oauth2.client.secret": "",
  "fs.azure.account.oauth2.client.endpoint": "https://login.microsoftonline.com/115ee48c-5146-4054-9f33-83e2bfe089fd/oauth2/token"
}

spark.conf.set("fs.azure.account.auth.type.docclassifierstoragecct.dfs.core.windows.net", "OAuth")
for key, val in configs.items():
    spark.conf.set(f"{key}.docclassifierstoragecct.dfs.core.windows.net", val)

#configuring spark parallelism
#**Parallelism**: Instead of single-node Python loops for clarity. For more scale, convert the work to Spark parallelism: read the list of file paths to a DataFrame and `mapPartitions` / `foreachPartition` or use UDFs to parallelize extraction and calls.

spark.conf.set("spark.sql.shuffle.partitions", "400")
spark.conf.set("spark.databricks.io.cache.enabled", "true")

#todo
#Large files / memory**: For very large PDFs or huge images you may want streaming approaches. The above copies files locally before processing; adapt if you have memory concerns.
#todo
#**Idempotency**: The code stores SHA256 for each saved image. You can use that to skip reprocessing duplicates.

#account = dbutils.secrets.get("kv-scope","storage_account_name")
account="docclassifierstoragecct"
container_raw   = "raw"
container_stage = "stage"
container_redacted = "redacted"
container_processed="docclassifiercontainer"
abfss = lambda container, path="": f"abfss://{container}@{account}.dfs.core.windows.net/{path}"


import base64, json, os

DOCUMENTINTEL_ENDPOINT = "https://documentsclassifier.cognitiveservices.azure.com/" #dbutils.secrets.get("kv-scope","di_endpoint")  # e.g., https://<res>.cognitiveservices.azure.com
DOCUMENTINTEL_KEY      = ""# dbutils.secrets.get("kv-scope","di_key")
DOCUMENTINTEL_MODEL_ID = "prebuilt-document"  # or your custom model

# --- Azure Configurations ---
AZURE_OPENAI_EMBEDDING_ENDPOINT = "https://tpapp.openai.azure.com/openai/deployments/text-embedding-3-large/embeddings?api-version=2024-02-01"
AZURE_OPENAI_API_KEY = ""
AZURE_SEARCH_ENDPOINT = "https://docsclassifieraisearch.search.windows.net"
AZURE_SEARCH_KEY = ""
AZURE_SEARCH_INDEX = "mydocs-knowledgeharvester-index"

AZURE_OPENAI_API_KEY = ""
AZURE_OPENAI_ENDPOINT = "https://YOUR-AZURE-OPENAI-ENDPOINT.openai.azure.com/"
AZURE_OPENAI_DEPLOYMENT = "gpt-4o-mini"
AZURE_OPENAI_API_VERSION="2024-12-01-preview"


# system folders
PATH_DOCS_IN   = abfss(container_raw,   "incoming/docs/")     # .pdf/.docx/.xlsx will be stored here
PATH_IMAGES_OUT= abfss(container_stage, "images/")            # extracted images go here
PATH_JSON_OUT  = abfss(container_stage, "extracted_json/")    # DI/Vision JSON here
PATH_CLEANED   = abfss(container_redacted, "cleaned/")           # PII-clean text
PATH_EMBED     = abfss(container_stage, "embeddings/")        # parquet with embeddings
PATH_TEXT_OUT = abfss(container_stage, "text/")
PARQUET_FOLDER_PATH = abfss(container_stage, "text/folder_name=Cloud Security Posture Management")


# COMMAND ----------

import pandas as pd
import io
import requests
from docx import Document

# --------------------------------------------
# Configuration
# --------------------------------------------

WORD_PATH = "https://docclassifierstoragecct.blob.core.windows.net/raw/incoming/docs/Cloud%20Security%20Posture%20Management.docx?sp=r&st=2025-10-07T21:34:51Z&se=2025-10-08T05:49:51Z&spr=https&sv=2024-11-04&sr=b&sig=rMRFyn5sQKeLzB1qwcotOAUGX5LMD304T83hhUC%2F4LE%3D"
PARQUET_PATH = PARQUET_FOLDER_PATH

# --------------------------------------------
# 1. Read Word file (extract tables)
# --------------------------------------------

response = requests.get(WORD_PATH)
word_bytes = io.BytesIO(response.content)
doc = Document(word_bytes)

df_word_tables = []
for table in doc.tables:
    data = []
    keys = None
    for i, row in enumerate(table.rows):
        text = [cell.text.strip() for cell in row.cells]
        if i == 0:
            keys = text
        else:
            data.append(text)
    if keys and data:
        df_word_tables.append(pd.DataFrame(data, columns=keys))
df_word = pd.concat(df_word_tables, ignore_index=True) if df_word_tables else pd.DataFrame()
df_word.columns = [col.strip() for col in df_word.columns]

# --------------------------------------------
# 2. Read all Parquet files in the folder using Spark
# --------------------------------------------

df_parquet = spark.read.parquet(PARQUET_PATH)
df_parquet_sample = df_parquet.toPandas()
df_parquet_sample.columns = [col.strip() for col in df_parquet_sample.columns]

# --------------------------------------------
# 3. Align columns and sort for comparison
# --------------------------------------------

common_cols = sorted(set(df_word.columns) & set(df_parquet_sample.columns))
df_word_sorted = df_word[common_cols].sort_values(by=common_cols).reset_index(drop=True)
df_parquet_sorted = df_parquet_sample[common_cols].sort_values(by=common_cols).reset_index(drop=True)

# --------------------------------------------
# 4. Compare entire content
# --------------------------------------------

if df_word_sorted.equals(df_parquet_sorted):
    print("✅ All content matches between Word and Parquet files in the folder.")
else:
    print("❌ Content mismatch detected between Word and Parquet files in the folder.")
    diff_word = df_word_sorted[~df_word_sorted.isin(df_parquet_sorted)].dropna(how='all')
    diff_parquet = df_parquet_sorted[~df_parquet_sorted.isin(df_word_sorted)].dropna(how='all')
    print("Rows in Word not in Parquet:\n", diff_word)
    print("Rows in Parquet not in Word:\n", diff_parquet)
    if common_cols:
        only_in_word = pd.merge(df_word_sorted, df_parquet_sorted, how='outer', on=common_cols, indicator=True).query('_merge == "left_only"').drop('_merge', axis=1)
        print("Data present in Word but not in Parquet:\n", only_in_word)
        if not only_in_word.empty:
            display(spark.createDataFrame(only_in_word))
    else:
        print("No common columns to perform merge on between Word and Parquet.")

# --------------------------------------------
# 5. Check partitioning completeness (row count and unique values)
# --------------------------------------------

if not df_word.empty and not df_parquet_sample.empty:
    if len(df_word_sorted) == len(df_parquet_sorted):
        print(f"✅ Row count matches: {len(df_word_sorted)} rows.")
    else:
        print(f"❌ Row count mismatch: Word has {len(df_word_sorted)}, Parquet has {len(df_parquet_sorted)}.")

    for col in common_cols:
        word_unique = set(df_word_sorted[col].dropna().unique())
        parquet_unique = set(df_parquet_sorted[col].dropna().unique())
        if word_unique != parquet_unique:
            print(f"❌ Unique values mismatch in column '{col}':")
            print("  In Word not in Parquet:", word_unique - parquet_unique)
            print("  In Parquet not in Word:", parquet_unique - word_unique)
        else:
            print(f"✅ Unique values match for column '{col}'.")