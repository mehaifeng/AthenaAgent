MinerU provides two document extract APIs to meet different use cases:

- 🎯 **Precision Extract API** — Requires a Token (create it yourself on the API Management page); supports single/batch files, table/formula recognition, and multi-format output
- ⚡ **Agent Lightweight Extract API** — No login required; IP rate-limited to prevent abuse; designed for AI Agent workflows

* * *

# Mode Comparison

| Dimension | 🎯 Precision Extract API | ⚡ Agent Lightweight Extract API |
| --- | --- | --- |
| Token Required | ✅ Yes | ❌ No (IP rate-limited) |
| Endpoint | `/api/v4/extract/task` or `/api/v4/file-urls/batch` | `/api/v1/agent/parse/url` or `/api/v1/agent/parse/file` |
| Model Version | `pipeline` (default) / `vlm` (recommended) / `MinerU-HTML` | Fixed pipeline lightweight model |
| Table/Formula Recognition | Supported (configurable) | Supported (configurable) |
| File Size Limit | ≤ 200MB | ≤ 10MB |
| Page Limit | ≤ 200 pages | ≤ 20 pages |
| Batch Support | ✅ Supported (≤ 200 files) | ❌ Single file only |
| Output Format | Zip archive containing Markdown and JSON; exportable to docx/html/latex | Markdown only (CDN link) |
| Invocation Method | Async (submit → poll) | Async (submit → poll) |

* * *

# 🎯 Precision Extract API

> Requires a Token (create it yourself on the API Management page). Supports pipeline / vlm / MinerU-HTML models, for both single-file and batch processing.

## Overview

MinerU's Precision Extract API is designed for complex documents that require high-accuracy, deep structural extraction. It can intelligently recognize and process various complex layouts and multimodal content (such as tables, mathematical formulas, charts, images, multi-column layouts, etc.), converting document content into high-quality structured data.

**Core Features:**

- **Ultimate Accuracy**: Delivers industry-leading extract accuracy, especially excelling at non-standard and complex documents
- **Deep Structuring**: Goes beyond simple text extraction to deeply understand document layout and semantics, outputting structured data with rich hierarchical relationships
- **Multimodal Support**: Comprehensive support for accurate recognition and extraction of text, tables, images, formulas, and other content types
- **Complex Layout Adaptation**: Effectively handles scanned documents, disordered typesetting, watermark interference, and other complex document scenarios

**File Limits:**

| Limit | Value |
| --- | --- |
| Max file size | 200 MB |
| Max page count | 200 pages |
| Supported formats | PDF, Image(png/jpg/jpeg/jp2/webp/gif/bmp), Doc, Docx, Ppt, Pptx, Xls, Xlsx |

* * *

## 1\. Single File Extract

### Create Extract Task

**Endpoint Description**

Used for creating extract tasks via API. Users must include a Token in the Header (create it yourself on the API Management page).
Notes:

- Maximum file size is 200MB, maximum page count is 200 pages
- Each account has a daily quota of 1,000 pages with highest priority; pages beyond 1,000 will have lower priority
- Due to network restrictions, URLs from GitHub, AWS, and other overseas services may time out
- This endpoint does not support direct file upload
- The header must include the Authorization field in the format: Bearer + space + Token

**Python Example (for PDF, Doc, PPT, Excel, image files):**

```python
import requests

token = "token created on the API Management page"
url = "https://mineru.net/api/v4/extract/task"
header = {
    "Content-Type": "application/json",
    "Authorization": f"Bearer {token}"
}
data = {
    "url": "https://cdn-mineru.openxlab.org.cn/demo/example.pdf",
    "model_version": "vlm"
}

res = requests.post(url,headers=header,json=data)
print(res.status_code)
print(res.json())
print(res.json()["data"])
```

**Python Example (for HTML files):**

```python
import requests

token = "token created on the API Management page"
url = "https://mineru.net/api/v4/extract/task"
header = {
    "Content-Type": "application/json",
    "Authorization": f"Bearer {token}"
}
data = {
    "url": "https://****",
    "model_version": "MinerU-HTML"
}

res = requests.post(url,headers=header,json=data)
print(res.status_code)
print(res.json())
print(res.json()["data"])
```

**CURL Example (for PDF, Doc, PPT, Excel, image files):**

```sh
curl --location --request POST 'https://mineru.net/api/v4/extract/task' \
--header 'Authorization: Bearer ***' \
--header 'Content-Type: application/json' \
--header 'Accept: */*' \
--data-raw '{
    "url": "https://cdn-mineru.openxlab.org.cn/demo/example.pdf",
    "model_version": "vlm"
}'
```

**CURL Example (for HTML files):**

```sh
curl --location --request POST 'https://mineru.net/api/v4/extract/task' \
--header 'Authorization: Bearer ***' \
--header 'Content-Type: application/json' \
--header 'Accept: */*' \
--data-raw '{
    "url": "https://****",
    "model_version": "MinerU-HTML"
}'
```

**Request Body Parameters**

| Parameter | Type | Required | Example | Description |
| --- | --- | --- | --- | --- |
| url | string | Yes | [https://cdn-mineru.openxlab.org.cn/demo/example.pdf](https://cdn-mineru.openxlab.org.cn/demo/example.pdf) | File URL, supports .pdf, .doc, .docx, .ppt, .pptx, Image(png/jpg/jpeg/jp2/webp/gif/bmp), .html formats |
| is\_ocr | bool | No | false | Whether to enable OCR, default false. Only effective for pipeline and vlm models |
| enable\_formula | bool | No | true | Whether to enable formula recognition, default true. Only effective for pipeline and vlm models. Note: for vlm model, this parameter only affects inline formula extract |
| enable\_table | bool | No | true | Whether to enable table recognition, default true. Only effective for pipeline and vlm models |
| language | string | No | ch | Document language. Default `ch`. See [Language Value Reference](https://mineru.net/apiManage/docs#language-value-reference) for supported values. Only effective for pipeline and vlm models |
| data\_id | string | No | abc\*\* | Data ID corresponding to the extract object. Consists of uppercase/lowercase letters, numbers, underscores (\_), hyphens (-), and periods (.), no more than 128 characters. Can be used to uniquely identify your business data. |
| callback | string | No | [http://127.0.0.1/callback](http://127.0.0.1/callback) | URL for callback notification of extract results. Supports HTTP and HTTPS protocols. If empty, you must poll for results periodically. The callback endpoint must support POST method, UTF-8 encoding, Content-Type:application/json, and parameters checksum and content.<br>checksum: String format, generated by SHA256 algorithm from the concatenation of user uid + seed + content. User UID can be found in the personal center. To prevent tampering, you can generate the string using the above algorithm when receiving push results and verify it against the checksum.<br>content: JSON string format, parse it yourself into a JSON object. For content result examples, refer to the task query result response, corresponding to the data part of the task query result.<br>Note: After your server callback endpoint receives the extract result push from MinerU, if it returns HTTP status code 200, it indicates successful reception. Any other HTTP status code is considered a failure. On failure, MinerU will retry pushing up to 5 times until successful. If still unsuccessful after 5 retries, no further pushes will be made. Please check your callback endpoint status. |
| seed | string | No | abc\*\* | Random string used for signing callback notification requests. Consists of letters, numbers, and underscores (\_), no more than 64 characters. Defined by you, used to verify that callback notifications are initiated by the MinerU extract service.<br>Note: This field is required when using callback. |
| extra\_formats | \[string\] | No | \["docx","html"\] | Markdown and JSON are default export formats and do not need to be set. This parameter only supports one or more of docx, html, and latex formats. Not applicable to HTML source files. |
| page\_ranges | string | No | 1-200 | Page range, formatted as a comma-separated string. E.g.: "2,4-6" selects page 2 and pages 4 to 6 (inclusive, result: \[2,4,5,6\]); "2--2" selects from page 2 to the second-to-last page ("-2" means second-to-last page). |
| model\_version | string | No | vlm | MinerU model version, three options: pipeline, vlm, MinerU-HTML. Default is pipeline. For HTML files, model\_version must be explicitly set to MinerU-HTML; for non-HTML files, choose pipeline or vlm |
| no\_cache | bool | No | false | Whether to bypass cache, default false. Our API server caches URL content for a period of time. Set to true to ignore cached results and fetch the latest content from the URL. |
| cache\_tolerance | int | No | 900 | Cache tolerance time (seconds), default 900 (15 minutes). The acceptable cache validity period for URL content. Caches exceeding this time will not be used. Effective when no\_cache is false |

**Response Parameters**

| Parameter | Type | Example | Description |
| --- | --- | --- | --- |
| code | int | 0 | Status code, success: 0 |
| msg | string | ok | Processing message, success: "ok" |
| trace\_id | string | c876cd60b202f2396de1f9e39a1b0172 | Request ID |
| data.task\_id | string | a90e6ab6-44f3-4554-b459-b62fe4c6b436 | Extraction task ID, used to query task results |

**Response Example**

```json
{
  "code": 0,
  "data": {
    "task_id": "a90e6ab6-44f3-4554-b4***"
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

### Get Task Result

**Endpoint Description**

Query the current progress of an extraction task by task\_id. Once the task is complete, the endpoint responds with the extraction details.

**Python Example**

```python
import requests

token = "token created on the API Management page"
task_id = "task_id returned from the previous step"
url = f"https://mineru.net/api/v4/extract/task/{task_id}"
header = {
    "Content-Type": "application/json",
    "Authorization": f"Bearer {token}"
}

res = requests.get(url, headers=header)
print(res.status_code)
print(res.json())
print(res.json()["data"])
```

**CURL Example**

```sh
curl --location --request GET 'https://mineru.net/api/v4/extract/task/{task_id}' \
--header 'Authorization: Bearer *****' \
--header 'Accept: */*'
```

**Response Parameters**

| Parameter | Type | Example | Description |
| --- | --- | --- | --- |
| code | int | 0 | Status code, success: 0 |
| msg | string | ok | Processing message, success: "ok" |
| trace\_id | string | c876cd60b202f2396de1f9e39a1b0172 | Request ID |
| data.task\_id | string | abc\*\* | Task ID |
| data.data\_id | string | abc\*\* | Data ID corresponding to the extract object.<br>Note: If data\_id was passed in the extract request, it will be returned here. |
| data.state | string | done | Task processing status: done (completed), pending (queued), running (extract), failed (extract failed), converting (format conversion in progress) |
| data.full\_zip\_url | string | [https://cdn-mineru.openxlab.org.cn/pdf/018e53ad-d4f1-475d-b380-36bf24db9914.zip](https://cdn-mineru.openxlab.org.cn/pdf/018e53ad-d4f1-475d-b380-36bf24db9914.zip) | Extract result zip archive. For non-HTML files, see: [https://opendatalab.github.io/MinerU/reference/output\_files/](https://opendatalab.github.io/MinerU/reference/output_files/) where layout.json corresponds to middle.json, \*\*\_model.json corresponds to model.json, \*\*\_content\_list.json corresponds to content\_list.json, and full.md is the Markdown result. For HTML files: full.md is the Markdown result, main.html is the extracted main content HTML |
| data.err\_msg | string | Unsupported file format, please upload a valid file type | Failure reason, valid when state=failed |
| data.extract\_progress.extracted\_pages | int | 1 | Number of pages parsed, valid when state=running |
| data.extract\_progress.start\_time | string | 2025-01-20 11:43:20 | Extract start time, valid when state=running |
| data.extract\_progress.total\_pages | int | 2 | Total number of pages, valid when state=running |

**Response Examples**

```json
{
  "code": 0,
  "data": {
    "task_id": "47726b6e-46ca-4bb9-******",
    "state": "running",
    "err_msg": "",
    "extract_progress": {
      "extracted_pages": 1,
      "total_pages": 2,
      "start_time": "2025-01-20 11:43:20"
    }
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

```json
{
  "code": 0,
  "data": {
    "task_id": "47726b6e-46ca-4bb9-******",
    "state": "done",
    "full_zip_url": "https://cdn-mineru.openxlab.org.cn/pdf/018e53ad-d4f1-475d-b380-36bf24db9914.zip",
    "err_msg": ""
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

## 2\. Batch File Extract

### Local batch File Upload Extract

**Endpoint Description**

Used for uploading local files for extract. You can batch-request file upload URLs through this endpoint. After uploading, the system will automatically submit extract tasks.
Notes:

- The file upload URL is valid for 24 hours. Please complete the upload within this period
- No Content-Type header is required when uploading files
- After file upload is complete, there is no need to call a submit task endpoint. The system will automatically scan uploaded files and submit extract tasks
- Maximum 50 URLs per request
- The header must include the Authorization field in the format: Bearer + space + Token

**Python Example (for PDF, Doc, PPT, Excel, image files):**

```python
import requests

token = "token created on the API Management page"
url = "https://mineru.net/api/v4/file-urls/batch"
header = {
    "Content-Type": "application/json",
    "Authorization": f"Bearer {token}"
}
data = {
    "files": [\
        {"name":"demo.pdf", "data_id": "abcd"}\
    ],
    "model_version":"vlm"
}
file_path = ["demo.pdf"]
try:
    response = requests.post(url,headers=header,json=data)
    if response.status_code == 200:
        result = response.json()
        print('response success. result:{}'.format(result))
        if result["code"] == 0:
            batch_id = result["data"]["batch_id"]
            urls = result["data"]["file_urls"]
            print('batch_id:{},urls:{}'.format(batch_id, urls))
            for i in range(0, len(urls)):
                with open(file_path[i], 'rb') as f:
                    res_upload = requests.put(urls[i], data=f)
                    if res_upload.status_code == 200:
                        print(f"{urls[i]} upload success")
                    else:
                        print(f"{urls[i]} upload failed")
        else:
            print('apply upload url failed,reason:{}'.format(result["msg"]))
    else:
        print('response not success. status:{} ,result:{}'.format(response.status_code, response))
except Exception as err:
    print(err)
```

**Python Example (for HTML files):**

```python
import requests

token = "token created on the API Management page"
url = "https://mineru.net/api/v4/file-urls/batch"
header = {
    "Content-Type": "application/json",
    "Authorization": f"Bearer {token}"
}
data = {
    "files": [\
        {"name":"demo.html", "data_id": "abcd"}\
    ],
    "model_version":"MinerU-HTML"
}
file_path = ["demo.html"]
try:
    response = requests.post(url,headers=header,json=data)
    if response.status_code == 200:
        result = response.json()
        print('response success. result:{}'.format(result))
        if result["code"] == 0:
            batch_id = result["data"]["batch_id"]
            urls = result["data"]["file_urls"]
            print('batch_id:{},urls:{}'.format(batch_id, urls))
            for i in range(0, len(urls)):
                with open(file_path[i], 'rb') as f:
                    res_upload = requests.put(urls[i], data=f)
                    if res_upload.status_code == 200:
                        print(f"{urls[i]} upload success")
                    else:
                        print(f"{urls[i]} upload failed")
        else:
            print('apply upload url failed,reason:{}'.format(result["msg"]))
    else:
        print('response not success. status:{} ,result:{}'.format(response.status_code, response))
except Exception as err:
    print(err)
```

**CURL Example (for PDF, Doc, PPT, Excel, image files):**

```sh
curl --location --request POST 'https://mineru.net/api/v4/file-urls/batch' \
--header 'Authorization: Bearer ***' \
--header 'Content-Type: application/json' \
--header 'Accept: */*' \
--data-raw '{
    "files": [\
        {"name":"demo.pdf", "data_id": "abcd"}\
    ],
    "model_version": "vlm"
}'
```

**CURL Example (for HTML files):**

```sh
curl --location --request POST 'https://mineru.net/api/v4/file-urls/batch' \
--header 'Authorization: Bearer ***' \
--header 'Content-Type: application/json' \
--header 'Accept: */*' \
--data-raw '{
    "files": [\
        {"name":"demo.html", "data_id": "abcd"}\
    ],
    "model_version": "MinerU-HTML"
}'
```

**CURL File Upload Example:**

```sh
curl -X PUT -T /path/to/your/file.pdf 'https://****'
```

**Request Body Parameters**

| Parameter | Type | Required | Example | Description |
| --- | --- | --- | --- | --- |
| enable\_formula | bool | No | true | Whether to enable formula recognition, default true. Only effective for pipeline and vlm models. Note: for vlm model, this parameter only affects inline formula extract |
| enable\_table | bool | No | true | Whether to enable table recognition, default true. Only effective for pipeline and vlm models |
| language | string | No | ch | Document language. Default `ch`. See [Language Value Reference](https://mineru.net/apiManage/docs#language-value-reference) for supported values. Only effective for pipeline and vlm models |
| file.‌name | string | Yes | demo.pdf | File name, supports .pdf, .doc, .docx, .ppt, .pptx, Image(png/jpg/jpeg/jp2/webp/gif/bmp), .html formats. We strongly recommend including the correct file extension |
| file.is\_ocr | bool | No | true | Whether to enable OCR, default false. Only effective for pipeline and vlm models |
| file.data\_id | string | No | abc\*\* | Data ID corresponding to the extract object. Consists of uppercase/lowercase letters, numbers, underscores (\_), hyphens (-), and periods (.), no more than 128 characters. Can be used to uniquely identify your business data. |
| file.page\_ranges | string | No | 1-200 | Page range, formatted as a comma-separated string. E.g.: "2,4-6" selects page 2 and pages 4 to 6 (inclusive, result: \[2,4,5,6\]); "2--2" selects from page 2 to the second-to-last page ("-2" means second-to-last page). |
| callback | string | No | [http://127.0.0.1/callback](http://127.0.0.1/callback) | URL for callback notification of extract results. Supports HTTP and HTTPS protocols. If empty, you must poll for results periodically. The callback endpoint must support POST method, UTF-8 encoding, Content-Type:application/json, and parameters checksum and content.<br>checksum: String format, generated by SHA256 algorithm from the concatenation of user uid + seed + content. User UID can be found in the personal center. To prevent tampering, you can generate the string using the above algorithm when receiving push results and verify it against the checksum.<br>content: JSON string format, parse it yourself into a JSON object. For content result examples, refer to the task query result response, corresponding to the data part of the task query result.<br>Note: After your server callback endpoint receives the extract result push from MinerU, if it returns HTTP status code 200, it indicates successful reception. Any other HTTP status code is considered a failure. On failure, MinerU will retry pushing up to 5 times until successful. If still unsuccessful after 5 retries, no further pushes will be made. Please check your callback endpoint status. |
| seed | string | No | abc\*\* | Random string used for signing callback notification requests. Consists of letters, numbers, and underscores (\_), no more than 64 characters. Defined by you, used to verify that callback notifications are initiated by the MinerU extract service.<br>Note: This field is required when using callback. |
| extra\_formats | \[string\] | No | \["docx","html"\] | Markdown and JSON are default export formats and do not need to be set. This parameter only supports one or more of docx, html, and latex formats. Not applicable to HTML source files. |
| model\_version | string | No | vlm | MinerU model version, three options: pipeline, vlm, MinerU-HTML. Default is pipeline. For HTML files, model\_version must be explicitly set to MinerU-HTML; for non-HTML files, choose pipeline or vlm |

**Response Parameters**

| Parameter | Type | Example | Description |
| --- | --- | --- | --- |
| code | int | 0 | Status code, success: 0 |
| msg | string | ok | Processing message, success: "ok" |
| trace\_id | string | c876cd60b202f2396de1f9e39a1b0172 | Request ID |
| data.batch\_id | string | 2bb2f0ec-a336-4a0a-b61a-\*\*\*\* | Batch extraction task ID, used to query batch extract results |
| data.file\_urls | \[string\] | \[" [https://mineru.oss-cn-shanghai.aliyuncs.com/api-upload/\*\*\*](https://mineru.oss-cn-shanghai.aliyuncs.com/api-upload/***)"\] | File upload URLs |

**Response Example**

```json
{
  "code": 0,
  "data": {
    "batch_id": "2bb2f0ec-a336-4a0a-b61a-241afaf9cc87",
    "file_urls": ["https://***"]
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

### Batch URL Upload Extract

**Endpoint Description**

Used for batch-creating extraction tasks via API.
Notes:

- Maximum 50 URLs per request
- Maximum file size is 200MB, maximum page count is 200 pages
- Due to network restrictions, URLs from GitHub, AWS, and other overseas services may time out
- The header must include the Authorization field in the format: Bearer + space + Token

**Python Example (for PDF, Doc, PPT, Excel, image files):**

```python
import requests

token = "token created on the API Management page"
url = "https://mineru.net/api/v4/extract/task/batch"
header = {
    "Content-Type": "application/json",
    "Authorization": f"Bearer {token}"
}
data = {
    "files": [\
        {"url":"https://cdn-mineru.openxlab.org.cn/demo/example.pdf", "data_id": "abcd"}\
    ],
    "model_version": "vlm"
}
try:
    response = requests.post(url,headers=header,json=data)
    if response.status_code == 200:
        result = response.json()
        print('response success. result:{}'.format(result))
        if result["code"] == 0:
            batch_id = result["data"]["batch_id"]
            print('batch_id:{}'.format(batch_id))
        else:
            print('submit task failed,reason:{}'.format(result["msg"]))
    else:
        print('response not success. status:{} ,result:{}'.format(response.status_code, response))
except Exception as err:
    print(err)
```

**Python Example (for HTML files):**

```python
import requests

token = "token created on the API Management page"
url = "https://mineru.net/api/v4/extract/task/batch"
header = {
    "Content-Type": "application/json",
    "Authorization": f"Bearer {token}"
}
data = {
    "files": [\
        {"url":"https://***", "data_id": "abcd"}\
    ],
    "model_version": "MinerU-HTML"
}
try:
    response = requests.post(url,headers=header,json=data)
    if response.status_code == 200:
        result = response.json()
        print('response success. result:{}'.format(result))
        if result["code"] == 0:
            batch_id = result["data"]["batch_id"]
            print('batch_id:{}'.format(batch_id))
        else:
            print('submit task failed,reason:{}'.format(result["msg"]))
    else:
        print('response not success. status:{} ,result:{}'.format(response.status_code, response))
except Exception as err:
    print(err)
```

**CURL Example (for PDF, Doc, PPT, Excel, image files):**

```sh
curl --location --request POST 'https://mineru.net/api/v4/extract/task/batch' \
--header 'Authorization: Bearer ***' \
--header 'Content-Type: application/json' \
--header 'Accept: */*' \
--data-raw '{
    "files": [\
        {"url":"https://cdn-mineru.openxlab.org.cn/demo/example.pdf", "data_id": "abcd"}\
    ],
    "model_version": "vlm"
}'
```

**CURL Example (for HTML files):**

```sh
curl --location --request POST 'https://mineru.net/api/v4/extract/task/batch' \
--header 'Authorization: Bearer ***' \
--header 'Content-Type: application/json' \
--header 'Accept: */*' \
--data-raw '{
    "files": [\
        {"url":"https://***", "data_id": "abcd"}\
    ],
    "model_version": "MinerU-HTML"
}'
```

**Request Body Parameters**

| Parameter | Type | Required | Example | Description |
| --- | --- | --- | --- | --- |
| enable\_formula | bool | No | true | Whether to enable formula recognition, default true. Only effective for pipeline and vlm models. Note: for vlm model, this parameter only affects inline formula extract |
| enable\_table | bool | No | true | Whether to enable table recognition, default true. Only effective for pipeline and vlm models |
| language | string | No | ch | Document language. Default `ch`. See [Language Value Reference](https://mineru.net/apiManage/docs#language-value-reference) for supported values. Only effective for pipeline and vlm models |
| file.url | string | Yes | [demo.pdf](https://cdn-mineru.openxlab.org.cn/demo/example.pdf) | File URL, supports .pdf, .doc, .docx, .ppt, .pptx, Image(png/jpg/jpeg/jp2/webp/gif/bmp), .html formats |
| file.is\_ocr | bool | No | true | Whether to enable OCR, default false. Only effective for pipeline and vlm models |
| file.data\_id | string | No | abc\*\* | Data ID corresponding to the extract object. Consists of uppercase/lowercase letters, numbers, underscores (\_), hyphens (-), and periods (.), no more than 128 characters. Can be used to uniquely identify your business data. |
| file.page\_ranges | string | No | 1-200 | Page range, formatted as a comma-separated string. E.g.: "2,4-6" selects page 2 and pages 4 to 6 (inclusive, result: \[2,4,5,6\]); "2--2" selects from page 2 to the second-to-last page ("-2" means second-to-last page). |
| callback | string | No | [http://127.0.0.1/callback](http://127.0.0.1/callback) | URL for callback notification of extract results. Supports HTTP and HTTPS protocols. If empty, you must poll for results periodically. The callback endpoint must support POST method, UTF-8 encoding, Content-Type:application/json, and parameters checksum and content.<br>checksum: String format, generated by SHA256 algorithm from the concatenation of user uid + seed + content. User UID can be found in the personal center. To prevent tampering, you can generate the string using the above algorithm when receiving push results and verify it against the checksum.<br>content: JSON string format, parse it yourself into a JSON object. For content result examples, refer to the task query result response, corresponding to the data part of the task query result.<br>Note: After your server callback endpoint receives the extract result push from MinerU, if it returns HTTP status code 200, it indicates successful reception. Any other HTTP status code is considered a failure. On failure, MinerU will retry pushing up to 5 times until successful. If still unsuccessful after 5 retries, no further pushes will be made. Please check your callback endpoint status. |
| seed | string | No | abc\*\* | Random string used for signing callback notification requests. Consists of letters, numbers, and underscores (\_), no more than 64 characters. Defined by you, used to verify that callback notifications are initiated by the MinerU extract service.<br>Note: This field is required when using callback. |
| extra\_formats | \[string\] | No | \["docx","html"\] | Markdown and JSON are default export formats and do not need to be set. This parameter only supports one or more of docx, html, and latex formats. Not applicable to HTML source files. |
| model\_version | string | No | vlm | MinerU model version, three options: pipeline, vlm, MinerU-HTML. Default is pipeline. For HTML files, model\_version must be explicitly set to MinerU-HTML; for non-HTML files, choose pipeline or vlm |
| no\_cache | bool | No | false | Whether to bypass cache, default false. Our API server caches URL content for a period of time. Set to true to ignore cached results and fetch the latest content from the URL. |
| cache\_tolerance | int | No | 900 | Cache tolerance time (seconds), default 900 (15 minutes). The acceptable cache validity period for URL content. Caches exceeding this time will not be used. Effective when no\_cache is false |

**Request Body Example**

```json
{
  "files": [\
    {\
      "url": "https://cdn-mineru.openxlab.org.cn/demo/example.pdf",\
      "data_id": "abcd"\
    }\
  ],
  "model_version": "vlm"
}
```

**Response Parameters**

| Parameter | Type | Example | Description |
| --- | --- | --- | --- |
| code | int | 0 | Status code, success: 0 |
| msg | string | ok | Processing message, success: "ok" |
| trace\_id | string | c876cd60b202f2396de1f9e39a1b0172 | Request ID |
| data.batch\_id | string | 2bb2f0ec-a336-4a0a-b61a-\*\*\*\* | Batch extraction task ID, used to query batch extract results |

**Response Example**

```json
{
  "code": 0,
  "data": {
    "batch_id": "2bb2f0ec-a336-4a0a-b61a-241afaf9cc87"
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

### Batch Get Task Results

**Endpoint Description**

Query batch extraction task progress by batch\_id.

**Python Example**

```python
import requests

token = "token created on the API Management page"
batch_id = "batch_id returned from the previous step"
url = f"https://mineru.net/api/v4/extract-results/batch/{batch_id}"
header = {
    "Content-Type": "application/json",
    "Authorization": f"Bearer {token}"
}

res = requests.get(url, headers=header)
print(res.status_code)
print(res.json())
print(res.json()["data"])
```

**CURL Example**

```sh
curl --location --request GET 'https://mineru.net/api/v4/extract-results/batch/{batch_id}' \
--header 'Authorization: Bearer *****' \
--header 'Accept: */*'
```

**Response Parameters**

| Parameter | Type | Example | Description |
| --- | --- | --- | --- |
| code | int | 0 | Status code, success: 0 |
| msg | string | ok | Processing message, success: "ok" |
| trace\_id | string | c876cd60b202f2396de1f9e39a1b0172 | Request ID |
| data.batch\_id | string | 2bb2f0ec-a336-4a0a-b61a-241afaf9cc87 | batch\_id |
| data.extract\_result.file\_name | string | demo.pdf | File name |
| data.extract\_result.state | string | done | Task processing status: done (completed), waiting-file (waiting for file upload), pending (queued), running (extract), failed (extract failed), converting (format conversion in progress) |
| data.extract\_result.full\_zip\_url | string | [https://cdn-mineru.openxlab.org.cn/pdf/018e53ad-d4f1-475d-b380-36bf24db9914.zip](https://cdn-mineru.openxlab.org.cn/pdf/018e53ad-d4f1-475d-b380-36bf24db9914.zip) | Extract result zip archive. For non-HTML files, see: [https://opendatalab.github.io/MinerU/reference/output\_files/](https://opendatalab.github.io/MinerU/reference/output_files/) where layout.json corresponds to middle.json, \*\*\_model.json corresponds to model.json, \*\*\_content\_list.json corresponds to content\_list.json, and full.md is the Markdown result. For HTML files: full.md is the Markdown result, main.html is the extracted main content HTML |
| data.extract\_result.err\_msg | string | Unsupported file format, please upload a valid file type | Failure reason, valid when state=failed |
| data.extract\_result.data\_id | string | abc\*\* | Data ID corresponding to the extract object.<br>Note: If data\_id was passed in the extract request, it will be returned here. |
| data.extract\_result.extract\_progress.extracted\_pages | int | 1 | Number of pages parsed, valid when state=running |
| data.extract\_result.extract\_progress.start\_time | string | 2025-01-20 11:43:20 | Extract start time, valid when state=running |
| data.extract\_result.extract\_progress.total\_pages | int | 2 | Total number of pages, valid when state=running |

**Response Example**

```json
{
  "code": 0,
  "data": {
    "batch_id": "2bb2f0ec-a336-4a0a-b61a-241afaf9cc87",
    "extract_result": [\
      {\
        "file_name": "example.pdf",\
        "state": "done",\
        "err_msg": "",\
        "full_zip_url": "https://cdn-mineru.openxlab.org.cn/pdf/018e53ad-d4f1-475d-b380-36bf24db9914.zip"\
      },\
      {\
        "file_name": "demo.pdf",\
        "state": "running",\
        "err_msg": "",\
        "extract_progress": {\
          "extracted_pages": 1,\
          "total_pages": 2,\
          "start_time": "2025-01-20 11:43:20"\
        }\
      }\
    ]
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

### Common Error Codes

| Error Code | Description | Solution |
| --- | --- | --- |
| A0202 | Invalid Token | Check if the Token is correct, verify the Bearer prefix, or replace with a new Token |
| A0211 | Token expired | Replace with a new Token |
| -500 | Parameter error | Ensure parameter types and Content-Type are correct |
| -10001 | Service error | Please try again later |
| -10002 | Request parameter error | Check request parameter format |
| -60001 | Failed to generate upload URL | Please try again later |
| -60002 | Failed to match file format | File type detection failed. Ensure the file name and URL contain the correct extension, and the file is one of: pdf, doc, docx, ppt, pptx, xls, xlsx, png, jp(e)g |
| -60003 | File read failure | Check if the file is corrupted and re-upload |
| -60004 | Empty file | Please upload a valid file |
| -60005 | File size exceeds limit | Check file size, maximum supported is 200MB |
| -60006 | Page count exceeds limit | Please split the file and try again |
| -60007 | Model service temporarily unavailable | Please try again later or contact technical support |
| -60008 | File read timeout | Check that the URL is accessible |
| -60009 | Task submission queue is full | Please try again later |
| -60010 | Extract failed | Please try again later |
| -60011 | Failed to get valid file | Please ensure the file has been uploaded |
| -60012 | Task not found | Please ensure the task\_id is valid and not deleted |
| -60013 | No permission to access this task | You can only access tasks you submitted |
| -60014 | Deleting a running task | Running tasks cannot be deleted at this time |
| -60015 | File conversion failed | You can manually convert to PDF and re-upload |
| -60016 | File conversion failed | Failed to convert file to the specified format. Try exporting in another format or retry |
| -60017 | Retry limit reached | Wait for future model upgrades and try again |
| -60018 | Daily extract task limit reached | Please try again tomorrow |
| -60019 | HTML file extract quota exhausted | Please try again tomorrow |
| -60020 | File splitting failed | Please try again later |
| -60021 | Failed to read page count | Please try again later |
| -60022 | Web page read failure | May be caused by network issues or rate limiting. Please try again later |

* * *

# ⚡ Agent Lightweight Extract API

> No login required, no Token needed. IP rate-limited to prevent abuse. Designed for AI Agent scenarios like OpenClaw, outputs Markdown only, zero-barrier access.

## Overview

The Agent Lightweight Extract API is designed for AI Agent scenarios like OpenClaw, providing fast, login-free document extract capabilities.

**Core Features:**

- **No Login Required**: IP rate-limited to prevent abuse, no Token needed
- **Lightweight & Fast**: PDF and images use the pipeline lightweight model with table/formula recognition disabled for maximum extract speed; Word and PPT are parsed using native Office API
- **Unified Output**: Outputs Markdown format only, returns CDN links
- **Dual Submission Modes**: URL extract and file upload are separate endpoints; file upload uses signed URL mode

**File Limitations:**

| Limitation | Value |
| --- | --- |
| Max File Size | 10 MB |
| Max Page Count | 20 pages |
| Supported File Types | PDF, Images (png/jpg/jpeg/jp2/webp/gif/bmp), Docx, PPTx, xlsx |

**IP Rate Limiting:**

- Each IP has a per-minute request submission limit
- Exceeding the limit will return HTTP 429 status code

* * *

## 1\. URL Extract Endpoint

**Endpoint Description**

Submit a remote file URL for extract. The backend automatically downloads and parses the file.

The endpoint operates asynchronously — upon successful submission, a `task_id` is returned, and you need to poll the query endpoint for results.

**Request URL**

```
POST https://mineru.net/api/v1/agent/parse/url
```

**Request Body Parameters (JSON)**

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| url | string | Required | Remote file URL, supports PDF, images, Doc/Docx, PPT/PPTx, Xlsx formats. HTML is not supported. |
| file\_name | string | Optional | File name (with extension), used to determine file type. If not provided, it will be parsed from the URL automatically. |
| enable\_table | bool | Optional | Whether to enable table recognition. Default `true`. Only effective for PDF files |
| is\_ocr | bool | Optional | Whether to enable OCR. Default `false`. Only effective for PDF files |
| enable\_formula | bool | Optional | Whether to enable formula recognition. Default `true`. Only effective for PDF files |
| language | string | Optional | Extract language, affects OCR recognition. Default `ch`. See [Language Value Reference](https://mineru.net/apiManage/docs#language-value-reference) for supported values. Only effective for PDF files |
| page\_range | string | Optional | Page range, only effective for PDF. Supports `from-to` (e.g. `1-10`) or single page (e.g. `5`). Comma-separated complex formats are not supported. |

**Notes:**

- No Authorization header required
- Request body is JSON format (`Content-Type: application/json`), multipart/form-data is not supported

**Python Example**

```python
import requests

url = "https://mineru.net/api/v1/agent/parse/url"

data = {
    "url": "https://cdn-mineru.openxlab.org.cn/demo/example.pdf",
    "language": "ch",
    "page_range": "1-10",
    "enable_table": True,
    "is_ocr": False,
    "enable_formula": True
}

res = requests.post(url, json=data)
print(res.json())
```

**CURL Example**

```sh
curl --location --request POST 'https://mineru.net/api/v1/agent/parse/url' \
--header 'Content-Type: application/json' \
--data-raw '{
    "url": "https://cdn-mineru.openxlab.org.cn/demo/example.pdf",
    "language": "ch",
    "page_range": "1-10",
    "enable_table": true,
    "is_ocr": false,
    "enable_formula": true
}'
```

**Response Parameters**

| Parameter | Type | Example | Description |
| --- | --- | --- | --- |
| code | int | 0 | Status code, success: 0 |
| msg | string | ok | Processing message, success: "ok" |
| trace\_id | string | c876cd60b202f2396de1f9e39a1b0172 | Request ID |
| data.task\_id | string | a90e6ab6-44f3-4554-b459-b62fe4c6b43605 | Extract task ID, used to query task results. |

**Response Example**

```json
{
  "code": 0,
  "data": {
    "task_id": "a90e6ab6-44f3-4554-b459-b62fe4c6b43605"
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

* * *

## 2\. Local file Upload Endpoint (Signed Upload)

**Endpoint Description**

Submit a file upload extract task. The endpoint uses a **signed upload mode**:

1. Call this endpoint with file name and other parameters to get `task_id` and an OSS signed upload URL (`file_url`)
2. Client uses `PUT` method to upload the file directly to `file_url`
3. After upload completes, the backend automatically detects and begins extract
4. Poll the query endpoint for extract results

**Request URL**

```
POST https://mineru.net/api/v1/agent/parse/file
```

**Request Body Parameters (JSON)**

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| file\_name | string | Required | File name (with extension), used to determine file type. |
| language | string | Optional | Extract language, affects OCR recognition. Default `ch`. See [Language Value Reference](https://mineru.net/apiManage/docs#language-value-reference) for supported values. Only effective for PDF files |
| enable\_table | bool | Optional | Whether to enable table recognition. Default `true`. Only effective for PDF files |
| is\_ocr | bool | Optional | Whether to enable OCR. Default `false`. Only effective for PDF files |
| enable\_formula | bool | Optional | Whether to enable formula recognition. Default `true`. Only effective for PDF files |
| page\_range | string | Optional | Page range, only effective for PDF. Supports `from-to` (e.g. `1-10`) or single page (e.g. `5`). Comma-separated complex formats are not supported. |

**Notes:**

- No Authorization header required
- Request body is JSON format (`application/json`)
- Batch upload is not supported; each request can only upload one file

**Response Parameters**

| Parameter | Type | Example | Description |
| --- | --- | --- | --- |
| code | int | 0 | Status code, success: 0 |
| msg | string | ok | Processing message, success: "ok" |
| trace\_id | string | c876cd60b202f2396de1f9e39a1b0172 | Request ID |
| data.task\_id | string | a90e6ab6-44f3-4554-b459-b62fe4c6b43605 | Extract task ID, used to query task results. |
| data.file\_url | string | [https://oss-mineru.../agent/a90e6ab6-...pdf](https://oss-mineru.../agent/a90e6ab6-...pdf) | OSS signed upload URL; client PUTs the file to this address |

**Response Example**

```json
{
  "code": 0,
  "data": {
    "task_id": "a90e6ab6-44f3-4554-b459-b62fe4c6b43605",
    "file_url": "https://oss-mineru.openxlab.org.cn/agent/a90e6ab6-...pdf?Expires=..."
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

**Python Example (Complete Signed Upload Flow)**

```python
import requests

# Step 1: Get signed upload URL
api_url = "https://mineru.net/api/v1/agent/parse/file"
data = {
    "file_name": "document.pdf",
    "language": "ch",
    "page_range": "1-10",
    "enable_table": True,
    "is_ocr": False,
    "enable_formula": True
}

res = requests.post(api_url, json=data)
result = res.json()
task_id = result["data"]["task_id"]
file_url = result["data"]["file_url"]

print(f"Task created, task_id: {task_id}")

# Step 2: PUT upload file to OSS
with open("document.pdf", "rb") as f:
    put_res = requests.put(file_url, data=f)
    print(f"File upload status: {put_res.status_code}")
```

**CURL Example**

```sh
# Step 1: Get signed upload URL
curl --location --request POST 'https://mineru.net/api/v1/agent/parse/file' \
--header 'Content-Type: application/json' \
--data-raw '{
    "file_name": "document.pdf",
    "language": "ch",
    "page_range": "1-10",
    "enable_table": true,
    "is_ocr": false,
    "enable_formula": true
}'

# Step 2: PUT upload file to the returned file_url
curl --location --request PUT '<file_url>' \
--data-binary '@document.pdf'
```

* * *

## 3\. Query Extract Result

**Endpoint Description**

Query the status and result of a extract task by `task_id`. Once the task is complete, the response includes a CDN download link for the Markdown result file.

**Request URL**

```
GET https://mineru.net/api/v1/agent/parse/{task_id}
```

**Python Example**

```python
import requests

task_id = "a90e6ab6-44f3-4554-b459-b62fe4c6b43605"
url = f"https://mineru.net/api/v1/agent/parse/{task_id}"

res = requests.get(url)
print(res.json())
```

**CURL Example**

```sh
curl --location --request GET 'https://mineru.net/api/v1/agent/parse/{task_id}'
```

**Response Parameters**

| Parameter | Type | Example | Description |
| --- | --- | --- | --- |
| code | int | 0 | Status code, success: 0 |
| msg | string | ok | Processing message, success: "ok" |
| trace\_id | string | c876cd60b202f2396de1f9e39a1b0172 | Request ID |
| data.task\_id | string | a90e6ab6-...05 | Task ID (same as the one returned at submission) |
| data.state | string | done | Task status: waiting-file (waiting for file upload, file upload mode only), uploading (downloading file), pending (queued), running (extract), done (completed), failed (failed) |
| data.markdown\_url | string | [https://cdn-mineru.../full.md](https://cdn-mineru.../full.md) | CDN download link for the Markdown result file, valid when state=done |
| data.err\_msg | string | file page count exceeds lightweight API limit | Error message, valid when state=failed |
| data.err\_code | int | -30003 | Error code, valid when state=failed. See error code table below |

**Response Example (Waiting for File Upload — File Upload Mode Only)**

```json
{
  "code": 0,
  "data": {
    "task_id": "a90e6ab6-44f3-4554-b459-b62fe4c6b43605",
    "state": "waiting-file"
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

**Response Example (Processing)**

```json
{
  "code": 0,
  "data": {
    "task_id": "a90e6ab6-44f3-4554-b459-b62fe4c6b43605",
    "state": "running"
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

**Response Example (Completed)**

```json
{
  "code": 0,
  "data": {
    "task_id": "a90e6ab6-44f3-4554-b459-b62fe4c6b43605",
    "state": "done",
    "markdown_url": "https://cdn-mineru.openxlab.org.cn/pdf/a90e6ab6-.../full.md"
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

**Response Example (Failed)**

```json
{
  "code": 0,
  "data": {
    "task_id": "a90e6ab6-44f3-4554-b459-b62fe4c6b43605",
    "state": "failed",
    "err_code": -30003,
    "err_msg": "file page count exceeds lightweight API limit (50 pages), please use the standard API"
  },
  "msg": "ok",
  "trace_id": "c876cd60b202f2396de1f9e39a1b0172"
}
```

* * *

## Complete Usage Examples (Python)

**URL Mode**

```python
def parse_by_url(url, language="ch", page_range=None, enable_table=True, is_ocr=False, enable_formula=True):
    """Submit a document extract task via URL and wait for the result."""
    # 1. Submit URL extract task
    data = {"url": url, "language": language, "enable_table": enable_table, "is_ocr": is_ocr, "enable_formula": enable_formula}
    if page_range:
        data["page_range"] = page_range

    resp = requests.post(f"{BASE_URL}/parse/url", json=data)
    result = resp.json()
    if result["code"] != 0:
        print(f"Submission failed: {result['msg']}")
        return None

    task_id = result["data"]["task_id"]
    print(f"Task submitted, task_id: {task_id}")

    # 2. Poll for result
    return poll_result(task_id)

def poll_result(task_id, timeout=300, interval=3):
    """Poll for extract result."""
    state_labels = {
        "uploading": "Downloading file",
        "pending": "Queued",
        "running": "Extracting",
        "waiting-file": "Waiting for file upload",
    }
    start = time.time()
    while time.time() - start < timeout:
        resp = requests.get(f"{BASE_URL}/parse/{task_id}")
        result = resp.json()
        state = result["data"]["state"]
        elapsed = int(time.time() - start)

        if state == "done":
            markdown_url = result["data"]["markdown_url"]
            print(f"[{elapsed}s] Extracting complete, Markdown download link: {markdown_url}")
            md_resp = requests.get(markdown_url)
            return md_resp.text

        if state == "failed":
            print(f"[{elapsed}s] extract failed: {result['data'].get('err_msg', 'Unknown error')}")
            return None

        print(f"[{elapsed}s] {state_labels.get(state, state)}...")
        time.sleep(interval)

    print(f"Polling timed out ({timeout}s), please manually query task_id: {task_id}")
    return None

# Usage example
content = parse_by_url("https://cdn-mineru.openxlab.org.cn/demo/example.pdf")
```

**File Upload Mode (Signed Upload)**

```python
import requests
import time

BASE_URL = "https://mineru.net/api/v1/agent"

def parse_by_file(file_path, language="ch", page_range=None, enable_table=True, is_ocr=False, enable_formula=True):
    """Submit a document extract task via file upload and wait for the result."""
    file_name = file_path.split("/")[-1].split("\\")[-1]

    # 1. Get signed upload URL
    data = {"file_name": file_name, "language": language, "enable_table": enable_table, "is_ocr": is_ocr, "enable_formula": enable_formula}
    if page_range:
        data["page_range"] = page_range

    resp = requests.post(f"{BASE_URL}/parse/file", json=data)
    result = resp.json()
    if result["code"] != 0:
        print(f"Failed to get upload URL: {result['msg']}")
        return None

    task_id = result["data"]["task_id"]
    file_url = result["data"]["file_url"]
    print(f"Task created, task_id: {task_id}")

    # 2. PUT upload file to OSS
    with open(file_path, "rb") as f:
        put_resp = requests.put(file_url, data=f)
        if put_resp.status_code not in (200, 201):
            print(f"File upload failed, HTTP {put_resp.status_code}")
            return None
    print("File uploaded successfully, waiting for extract...")

    # 3. Poll for result
    return poll_result(task_id)

def poll_result(task_id, timeout=300, interval=3):
    """Poll for extract result."""
    state_labels = {
        "pending": "Queued",
        "running": "Extracting",
        "waiting-file": "Waiting for file upload",
    }
    start = time.time()
    while time.time() - start < timeout:
        resp = requests.get(f"{BASE_URL}/parse/{task_id}")
        result = resp.json()
        state = result["data"]["state"]
        elapsed = int(time.time() - start)

        if state == "done":
            markdown_url = result["data"]["markdown_url"]
            print(f"[{elapsed}s] extract complete, Markdown download link: {markdown_url}")
            md_resp = requests.get(markdown_url)
            return md_resp.text

        if state == "failed":
            print(f"[{elapsed}s] extract failed: {result['data'].get('err_msg', 'Unknown error')}")
            return None

        print(f"[{elapsed}s] {state_labels.get(state, state)}...")
        time.sleep(interval)

    print(f"Polling timed out ({timeout}s), please manually query task_id: {task_id}")
    return None

# Usage example
content = parse_by_file("./document.pdf")
```

* * *

## Agent-Specific Error Codes

| Error Code | Description | Agent Response Strategy |
| --- | --- | --- |
| -30001 | File size exceeds lightweight API limit (10MB) | Use the standard API or split the file |
| -30002 | File type not supported by lightweight API | Please upload PDF/Image/Doc/PPT/Excel |
| -30003 | Page count exceeds lightweight API limit | Use the standard API or specify page\_range |
| -30004 | Request parameter error | Check if required parameters are missing |

* * *

## Language Value Reference

Use the `language` field with one of the values below. The default value is `ch`.

#### Standalone language packs

| Value | Included languages | Notes |
| --- | --- | --- |
| `ch` | Chinese, English, Chinese Traditional | Chinese + English (default) |
| `ch_server` | Chinese, English, Chinese Traditional, Japanese | Traditional Chinese and handwritten-heavy content |
| `en` | English | English only |
| `japan` | Chinese, English, Chinese Traditional, Japanese | Japanese-first documents |
| `korean` | Korean, English | Korean |
| `chinese_cht` | Chinese, English, Chinese Traditional, Japanese | Traditional Chinese-first documents |
| `ta` | Tamil, English | Tamil |
| `te` | Telugu, English | Telugu |
| `ka` | Kannada | Kannada |
| `el` | Greek, English | Greek |
| `th` | Thai, English | Thai |

#### Language family packs

| Value | Script/Family | Included languages |
| --- | --- | --- |
| `latin` | Latin script | French, German, Afrikaans, Italian, Spanish, Bosnian, Portuguese, Czech, Welsh, Danish, Estonian, Irish, Croatian, Uzbek, Hungarian, Serbian (Latin), Indonesian, Occitan, Icelandic, Lithuanian, Maori, Malay, Dutch, Norwegian, Polish, Slovak, Slovenian, Albanian, Swedish, Swahili, Tagalog, Turkish, Latin, Azerbaijani, Kurdish, Latvian, Maltese, Pali, Romanian, Vietnamese, Finnish, Basque, Galician, Luxembourgish, Romansh, Catalan, Quechua |
| `arabic` | Arabic script | Arabic, Persian, Uyghur, Urdu, Pashto, Kurdish, Sindhi, Balochi, English |
| `cyrillic` | Cyrillic script | Russian, Belarusian, Ukrainian, Serbian (Cyrillic), Bulgarian, Mongolian, Abkhazian, Adyghe, Kabardian, Avar, Dargin, Ingush, Chechen, Lak, Lezgin, Tabasaran, Kazakh, Kyrgyz, Tajik, Macedonian, Tatar, Chuvash, Bashkir, Malian, Moldovan, Udmurt, Komi, Ossetian, Buryat, Kalmyk, Tuvan, Sakha, Karakalpak, English |
| `east_slavic` | East Slavic | Russian, Belarusian, Ukrainian, English |
| `devanagari` | Devanagari script | Hindi, Marathi, Nepali, Bihari, Maithili, Angika, Bhojpuri, Magahi, Santali, Newari, Konkani, Sanskrit, Haryanvi, English |

![Description](https://webpub.shlab.tech/dps/openxlabs/xlab-dataset/mineru-fe/mineru-seo-fe/mineru-seo-prod.31.afbb4cb8/_next/static/media/logo.f876e2bb.svg)

More Tools

Label LLM

Label U

MinerU Chem

MinerU Fans

Dingo

Legal Agreements

Terms of Service

Privacy Policy

Contact Us

https://github.com/opendatalab/MinerU

OpenDataLab@pjlab.org.cn

![Contact Us](https://mineru.net/_next/image?url=https%3A%2F%2Fwebpub.shlab.tech%2Fdps%2Fopenxlabs%2Fxlab-dataset%2Fmineru-fe%2Fmineru-seo-fe%2Fmineru-seo-prod.31.afbb4cb8%2F_next%2Fstatic%2Fmedia%2FqrCode-1.144cd067.png&w=256&q=75)

Contact Us

![Join Community](https://mineru.net/_next/image?url=https%3A%2F%2Fwebpub.shlab.tech%2FMinerU%2Fstatic%2Fimage%2Fmineru-wechat-community.jpg&w=256&q=75)

Join Community

© 2025 MinerU. All Rights Reserved.粤ICP备2025363392号

![Description](https://webpub.shlab.tech/dps/openxlabs/xlab-dataset/mineru-fe/mineru-seo-fe/mineru-seo-prod.31.afbb4cb8/_next/static/media/logo.f876e2bb.svg)

Legal Agreements

Terms of Service

Privacy Policy

Contact Us

© 2025 MinerU. All Rights Reserved.粤ICP备2025363392号