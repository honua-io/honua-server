# Database Connections Guide

Database connections are the foundation of Honua Server - they link your PostGIS databases to the web services. You must configure at least one connection before publishing layers.

## 📋 **Prerequisites**

- PostgreSQL database with PostGIS extension
- Database credentials (username/password)
- Network access from Honua Server to database

## 🎯 **Overview Workflow**

1. Navigate to Connections page
2. Add new database connection
3. Configure connection parameters
4. Test connection health
5. Use connection for layer publishing

---

## **Step 1: Navigate to Connections**

Access the connections management page:

1. Open Admin UI at `/admin`
2. Click **🔌 Connections** in the sidebar

*🖼️ Screenshot needed: Connections page - initial state (empty list)*

---

## **Step 2: Add New Connection**

Create your first database connection:

1. Click **➕ Add Connection** button
2. Connection form dialog opens

*🖼️ Screenshot needed: Add Connection dialog - empty form*

---

## **Step 3: Configure Connection**

Fill out the connection form with your database details:

### **Required Fields:**
- **Name**: Descriptive name (e.g., "primary-db", "analytics-db")
- **Host**: Database server hostname or IP
- **Port**: Database port (default: 5432)
- **Database**: Database name
- **Username**: Database username
- **Password**: Database password ⚠️ *Required*

### **Optional Fields:**
- **Description**: Additional details about this connection
- **SSL**: Enable SSL connection (Require/Prefer/Allow)

### **Example Configuration:**
```
Name: primary-db
Description: Main PostGIS database
Host: localhost
Port: 5432
Database: honua
Username: postgres
Password: [your-password]
SSL: Require
```

*🖼️ Screenshot needed: Form filled with sample data*

---

## **Step 4: Handle Validation**

The form validates required fields:

- **Missing password**: Shows "Password is required" error
- **Invalid port**: Shows port validation error
- **Empty name**: Shows "Name is required" error

*🖼️ Screenshot needed: Form showing validation errors*

---

## **Step 5: Save Connection**

1. Click **💾 Save** to create the connection
2. Form closes and returns to connections list
3. New connection appears in the list

*🖼️ Screenshot needed: Connections list showing newly created connection*

---

## **Step 6: Test Connection Health**

Verify your connection works:

1. Find your connection in the list
2. Click **🔍 Test** button
3. Status updates from "Unknown" → "Healthy" or shows error

*🖼️ Screenshot needed: Connection test in progress and results*

### **Health Status Indicators:**
- **🟢 Healthy**: Connection successful
- **🔴 Unhealthy**: Connection failed
- **🟡 Unknown**: Not tested yet

---

## **Step 7: Manage Existing Connections**

### **Edit Connection**
1. Click **✏️ Edit** for any connection
2. Modify connection parameters
3. Click **💾 Save** to apply changes

*🖼️ Screenshot needed: Edit connection dialog*

### **Delete Connection**
1. Click **🗑️ Delete** for any connection
2. Confirm deletion in dialog
3. Connection removed from list

*🖼️ Screenshot needed: Delete confirmation dialog*

**⚠️ Warning**: Deleting a connection will affect any published layers using it.

---

## **Connection List Overview**

The connections list shows:

- **Name**: Connection display name
- **Description**: Optional description text
- **Host**: Database server location
- **Database**: Target database name
- **Status**: Health indicator (Healthy/Unhealthy/Unknown)
- **Actions**: Edit, Delete, Test buttons

*🖼️ Screenshot needed: Full connections list with multiple connections*

---

## 🔧 **Troubleshooting Connections**

### **Common Connection Issues**

**"Connection timeout"**
- Check network connectivity to database server
- Verify firewall rules allow connections on database port
- Confirm database server is running

**"Authentication failed"**
- Verify username/password are correct
- Check user has appropriate database permissions
- Ensure user can connect from Honua Server's IP

**"Database does not exist"**
- Confirm database name spelling
- Verify database exists on the server
- Check user has access to the specified database

**"SSL connection required"**
- Set SSL mode to "Require" if database enforces SSL
- Check SSL certificates if using custom CA

### **Required Database Permissions**

Your database user needs these minimum permissions:
```sql
-- Connect to database
GRANT CONNECT ON DATABASE your_database TO your_user;

-- Read schema and tables
GRANT USAGE ON SCHEMA public TO your_user;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO your_user;

-- For layer publishing
GRANT CREATE ON SCHEMA public TO your_user;

-- For data import
GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO your_user;
```

### **Testing Connection Outside Honua**

Test your connection independently:

```bash
# Using psql
psql -h your-host -p 5432 -U your-username -d your-database

# Using connection string
psql "Host=your-host;Port=5432;Database=your-database;Username=your-username;Password=your-password"
```

---

## ➡️ **Next Steps**

After configuring database connections:

1. **[Publish Layers](layers-guide.md)** - Make your database tables available as web services
2. **[Import Data](import-guide.md)** - Add data to your database
3. **[Preview Data](preview-guide.md)** - View your data on a map

---

## 🔗 **Related Documentation**

- [Layer Publishing Guide](layers-guide.md) - Use connections to publish layers
- [Security Configuration](../../devops/SECURITY_CONFIGURATION.md) - Secure connection strings
- [Database Connection Issues](../../devops/troubleshooting/database-connection-issues.md) - PostGIS setup and troubleshooting

---
*Connection management provides the foundation for all Honua Server functionality. Ensure your connections are healthy before proceeding to layer publishing.*