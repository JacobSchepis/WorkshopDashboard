-- Demo JMS schema: the subset of JMS's tables and columns this app's queries use.
-- Generated from the app's own SQL (JmsQueries.cs); data comes from DemoSeeder.

CREATE TABLE IF NOT EXISTS company (
    company_id INTEGER PRIMARY KEY,
    company_name TEXT,
    address TEXT,
    shipping_address TEXT,
    creation_date TEXT,
    email TEXT,
    default_ship_to TEXT
);

CREATE TABLE IF NOT EXISTS company_client (
    company_id INTEGER PRIMARY KEY,
    labour_pref INTEGER,
    travel_pref INTEGER,
    part_pref INTEGER,
    active_status TEXT,
    standard_labour_rate REAL
);

CREATE TABLE IF NOT EXISTS company_people (
    company_people_id INTEGER PRIMARY KEY,
    full_name TEXT,
    phone_no TEXT,
    mobile TEXT,
    notes TEXT,
    title TEXT,
    first_name TEXT,
    last_name TEXT,
    expired TEXT,
    email TEXT,
    company_id INTEGER,
    company_department_id INTEGER,
    active_status TEXT
);

CREATE TABLE IF NOT EXISTS equipment (
    equipment_id INTEGER PRIMARY KEY,
    model_id INTEGER,
    serial_no TEXT,
    current_company_id INTEGER,
    generic_name TEXT,
    model_name_no TEXT,
    expired TEXT,
    comment TEXT,
    barcode_no TEXT,
    asset_no TEXT,
    last_inspected_date TEXT,
    creation_date TEXT,
    asset_condition INTEGER
);

CREATE TABLE IF NOT EXISTS equipment_certificate (
    equipment_certificate_id INTEGER PRIMARY KEY,
    filename TEXT,
    creation_date TEXT,
    equipment_id INTEGER,
    expiry_date TEXT
);

CREATE TABLE IF NOT EXISTS inspection (
    inspection_id INTEGER PRIMARY KEY,
    equipment_id INTEGER,
    user_id INTEGER,
    job_id INTEGER,
    creation_date TEXT,
    operational_status_id INTEGER,
    comments TEXT,
    rs_report_id INTEGER,
    asset_condition INTEGER
);

CREATE TABLE IF NOT EXISTS item_master (
    item_master_id INTEGER PRIMARY KEY,
    description TEXT,
    track_serial TEXT,
    creation_date TEXT,
    active_status TEXT
);

CREATE TABLE IF NOT EXISTS job (
    job_id INTEGER PRIMARY KEY,
    order_number TEXT,
    contact_name INTEGER,
    billing_address TEXT,
    shipping_address TEXT,
    owner_company_id INTEGER,
    job_status_id INTEGER,
    user_id INTEGER,
    notes TEXT,
    start_date TEXT,
    last_worked_date TEXT,
    completed_date TEXT,
    creation_date TEXT,
    job_no TEXT,
    job_priority_id INTEGER,
    rs_report_id INTEGER,
    standard_labour_rate REAL
);

CREATE TABLE IF NOT EXISTS job_equipment (
    job_equipment_id INTEGER PRIMARY KEY,
    job_id INTEGER,
    equipment_id INTEGER,
    creation_date TEXT,
    active_status TEXT
);

CREATE TABLE IF NOT EXISTS job_invoice (
    invoice_id INTEGER PRIMARY KEY,
    job_id INTEGER,
    creation_date TEXT,
    total_amount REAL,
    paid_amount REAL,
    file_location TEXT,
    job_no TEXT
);

CREATE TABLE IF NOT EXISTS job_quote (
    job_quote_id INTEGER PRIMARY KEY,
    job_id INTEGER,
    order_no TEXT,
    contact_id INTEGER,
    date_created TEXT,
    date_accepted TEXT,
    total_amount REAL,
    accepted TEXT,
    file_location TEXT
);

CREATE TABLE IF NOT EXISTS job_status (
    job_status_id INTEGER PRIMARY KEY,
    job_status TEXT
);

CREATE TABLE IF NOT EXISTS job_status_history (
    job_status_history_id INTEGER PRIMARY KEY,
    job_id INTEGER,
    job_status_id INTEGER,
    creation_date TEXT,
    user_id INTEGER,
    notes TEXT
);

CREATE TABLE IF NOT EXISTS labour (
    labour_id INTEGER PRIMARY KEY,
    user_id INTEGER,
    creation_date TEXT,
    start_date TEXT,
    end_date TEXT,
    job_id INTEGER,
    equipment_id INTEGER,
    description TEXT,
    timesheet_duration REAL,
    approved TEXT
);

CREATE TABLE IF NOT EXISTS model (
    model_id INTEGER PRIMARY KEY,
    manufacture_id INTEGER,
    model_name_no TEXT,
    description TEXT,
    creation_date TEXT
);

CREATE TABLE IF NOT EXISTS online_resource (
    online_resource_id INTEGER PRIMARY KEY,
    title TEXT,
    file_location TEXT,
    notes TEXT,
    creation_date TEXT,
    user_id INTEGER,
    active_status TEXT
);

CREATE TABLE IF NOT EXISTS online_resource_link (
    id INTEGER PRIMARY KEY,
    online_resource_id INTEGER,
    link_table_key_name TEXT,
    link_table_key_value INTEGER,
    pages TEXT
);

CREATE TABLE IF NOT EXISTS operational_status (
    operational_status_id INTEGER PRIMARY KEY,
    operational_status TEXT,
    active TEXT
);

CREATE TABLE IF NOT EXISTS order_status (
    order_status_id INTEGER PRIMARY KEY,
    order_status TEXT,
    active_status TEXT
);

CREATE TABLE IF NOT EXISTS recall (
    recall_id INTEGER PRIMARY KEY,
    model_id INTEGER,
    tga_recall_reference TEXT,
    notes TEXT,
    active INTEGER,
    user_id INTEGER,
    creation_date TEXT
);

CREATE TABLE IF NOT EXISTS service_type (
    service_type_id INTEGER PRIMARY KEY,
    service_type TEXT,
    description TEXT,
    active_status TEXT
);

CREATE TABLE IF NOT EXISTS services (
    service_id INTEGER PRIMARY KEY,
    job_id INTEGER,
    equipment_id INTEGER,
    service_type_id INTEGER,
    creation_date TEXT,
    user_id INTEGER,
    description TEXT,
    cost_price REAL,
    quantity INTEGER,
    deleted TEXT,
    item_master_id INTEGER,
    approved TEXT
);

CREATE TABLE IF NOT EXISTS stock (
    stock_id INTEGER PRIMARY KEY,
    item_master_id INTEGER,
    creation_date TEXT,
    vendor_item_order_id INTEGER,
    vendor_item_order_vuom_id INTEGER,
    job_id INTEGER,
    serial_no TEXT,
    quantity INTEGER,
    vendor_company_id INTEGER,
    user_id INTEGER,
    equipment_id INTEGER,
    stock_status_id INTEGER,
    vendor_part_code TEXT
);

CREATE TABLE IF NOT EXISTS test_script_item (
    test_script_item_id INTEGER PRIMARY KEY,
    test_type_id INTEGER,
    units TEXT,
    active_status TEXT
);

CREATE TABLE IF NOT EXISTS test_script_result (
    test_script_result_id INTEGER PRIMARY KEY,
    inspection_id INTEGER,
    test_script_item_id INTEGER,
    test_equipment_id INTEGER,
    creation_date TEXT,
    result_value TEXT,
    comment TEXT
);

CREATE TABLE IF NOT EXISTS user (
    user_id INTEGER PRIMARY KEY,
    first_name TEXT,
    last_name TEXT,
    title TEXT,
    company_id INTEGER,
    creation_date TEXT,
    company_people_id INTEGER
);

CREATE TABLE IF NOT EXISTS user_notification (
    user_notifications_id INTEGER PRIMARY KEY,
    user_id INTEGER,
    creation_date TEXT,
    notification_link TEXT,
    notification_message TEXT,
    notification_title TEXT
);

CREATE TABLE IF NOT EXISTS vendor_item_order (
    vendor_item_order_id INTEGER PRIMARY KEY,
    company_id INTEGER,
    notes TEXT,
    date_ordered TEXT,
    date_expected TEXT,
    user_id INTEGER,
    company_people_id INTEGER,
    fully_paid TEXT,
    order_status_id INTEGER,
    fully_delivered TEXT,
    date_last_delivered TEXT
);

CREATE TABLE IF NOT EXISTS vendor_item_order_vuom (
    vendor_item_order_vuom_id INTEGER PRIMARY KEY,
    vendor_item_order_id INTEGER,
    vendor_company_id INTEGER,
    master_item_id INTEGER,
    vendor_part_code TEXT,
    vendor_part_description TEXT,
    order_qty_vuom INTEGER,
    delivered_qty_vuom INTEGER,
    invoiced_qty_vuom INTEGER,
    total_order_cost_sd REAL,
    job_no INTEGER,
    equipment_id INTEGER,
    serial_no TEXT,
    active_status TEXT,
    creation_date TEXT
);
